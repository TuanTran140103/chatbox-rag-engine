# AGENTS.md - MarkdownGenQAs

## Project Overview
- **Type**: .NET 10 ASP.NET Core Web API
- **Single solution**: `MarkdownGenQAs.sln` → `MarkdownGenQAs.csproj`
- **Purpose**: Backend system that processes documents (PDF/DOCX), generates OCR content and Q&A pairs using LLM, with RBAC-based sharing

## Run Commands
```bash
dotnet build MarkdownGenQAs.sln
dotnet run --project MarkdownGenQAs.csproj
```
- HTTP: `http://localhost:5184`
- HTTPS: `https://localhost:7128` (plus http://localhost:5184)
- **Hangfire dashboard**: `/hangfire`
- **API docs** (dev only): `/docs` (Scalar)

## Required Services
The app **will not start** without these running locally:
- **PostgreSQL** (port 5432) - configured in `ConnectionStrings.DefaultConnection`
- **Redis** (port 6379) - configured in `Hangfire.RedisConnection`
- **MinIO** (port 9000) - S3-compatible storage, configured in `AWS.ServiceURL`
- **TokenCountService** (port 8000) - external service for token counting
- **OCRService** (port 5258) - external OCR service

## Environment Variables
`.env` file is loaded via `DotNetEnv` **before** `appsettings.json`. Contains:
- `LlmProviders__Nvidia__ApiKey` - Nvidia NIM API key
- `POSTGRES_PASSWORD` - database password

---

## Database Schema (RBAC v2)

### Entity Relationship Overview
```
OrganizationUnit (OU)
  ├── UserPosition (user ↔ OU relationship)
  ├── Dataset (neo at OU)
  │     ├── DatasetItem (folder/document tree)
  │     │     └── Document (actual file)
  │     └── AccessShare (granular permissions)
  └── (Self-referencing hierarchy via ParentId)
```

### Core Entities

#### OrganizationUnit
Cây phân cấp tổ chức (Materialized Path pattern)
```csharp
public class OrganizationUnit : BaseEntity
{
    public string Name { get; set; }           // Tên phòng/ban
    public string? Code { get; set; }          // Mã code (VD: "IT", "HR")
    public Guid? ParentId { get; set; }        // OU cha
    public string Path { get; set; }            // Materialized path: "/rootId/childId/..."
    public int Level { get; set; }             // Độ sâu trong cây
}
```

#### UserPosition
User thuộc về OU nào, với vai trò gì
```csharp
public class UserPosition : BaseEntity
{
    public Guid UserId { get; set; }           // User
    public Guid OUId { get; set; }             // OU mà user thuộc về
    public OrganizationRole Role { get; set; } // Staff = 0, Manager = 1
    public bool IsPrimary { get; set; }         // Vị trí chính của user
}
```

#### Dataset
Dataset neo tại một OU, chứa cây thư mục và documents
```csharp
public class Dataset : BaseEntity
{
    public string Name { get; set; }               // Tên dataset
    public string? Description { get; set; }       // Mô tả
    public int CountDocument { get; set; }         // Số documents

    public Guid OwnerUserId { get; set; }          // Chủ sở hữu dataset
    public Guid? OUId { get; set; }                // OU mà dataset thuộc về
    public bool IsPublicToUnit { get; set; }      // Tự động share Read cho OU + con
}
```

#### DatasetItem
Node trong cây thư mục của Dataset (Folder hoặc Document reference)
```csharp
public class DatasetItem : BaseEntity
{
    public Guid DatasetId { get; set; }            // Dataset cha
    public string Name { get; set; }                // Tên file/folder
    public DatasetItemType ItemType { get; set; }   // Folder = 0, Document = 1

    public string Path { get; set; }                // Path trong cây: "/folder1/folder2/"
    public int Level { get; set; }                  // Độ sâu

    public Guid? ParentId { get; set; }            // Parent trong cây
    public Guid? DocumentId { get; set; }          // Link đến Document (nếu ItemType = Document)

    public int SortOrder { get; set; }             // Thứ tự sắp xếp
}
```

#### Document
Tài liệu thực tế (PDF/DOCX), đã qua OCR và có Q&A
```csharp
public class Document : BaseEntity
{
    public string FileName { get; set; }           // Tên file gốc
    public string ObjectKeyFilePdf { get; set; }   // S3 object key

    public StatusDocument Status { get; set; }
    public bool IsOcred { get; set; }
    public bool IsQaGenerated { get; set; }

    public string? OcrContent { get; set; }        // Nội dung OCR (Markdown)
    public string? OcrPreview { get; set; }        // Preview ngắn
    public string? QaContent { get; set; }         // Q&A content (JSON)
    public string? SummaryContent { get; set; }    // Summary

    public Guid? UserId { get; set; }              // Ai upload (khác với dataset owner)
    public Guid? DatasetItemId { get; set; }       // DatasetItem chứa document này
}
```

#### AccessShare
Bảng chia sẻ quyền - hỗ trợ share cấp Dataset hoặc DatasetItem riêng lẻ
```csharp
public class AccessShare : BaseEntity
{
    public Guid DatasetId { get; set; }                    // Dataset được share
    public Guid? DatasetItemId { get; set; }               // NULL = share cả dataset, NOT NULL = share lẻ item

    public Guid? ShareToUserId { get; set; }              // Share cho user cụ thể
    public Guid? ShareToOUId { get; set; }                 // Share cho cả OU

    public DatasetPermissions PermissionMask { get; set; }  // Bitwise permissions
    public Guid GrantedBy { get; set; }                    // Ai share
}
```

---

## RBAC / Permission System

### Permission Flags (Bitwise)
```csharp
[Flags]
public enum DatasetPermissions
{
    None       = 0,    // 0000
    Read       = 1,    // 0001
    Update     = 2,    // 0010
    Delete     = 4,    // 0100
    Share      = 8,    // 1000

    Collaborate = Read | Update,              // 0011 (3)
    FullControl = Read | Update | Delete | Share  // 1111 (15)
}
```

### Permission Resolution (Effective Mask)
```
EffectiveMask = (Default Permissions) | (Shared Permissions)

Default Permissions:
- Owner của Dataset: FullControl (15)
- Manager của OU mà Dataset thuộc về: FullControl (15)
- Manager cấp trên (qua Path): Read (1)
- User cùng OU + Dataset.IsPublicToUnit=true: Read (1)

Shared Permissions:
- Từ AccessShares, ưu tiên quyền cao nhất nếu trùng lặp
```

### Visibility Rules
- **Staff**: Chỉ thấy người trong cùng OU
- **Manager**: Thấy OU mình, các OU con, Manager cấp trên và Manager cùng cấp

---

## Architecture
```
Controllers/
  ├── DocumentController.cs      # /api/v1/files - document upload/download
  ├── DatasetController.cs        # Dataset CRUD & sharing
  ├── OrganizationUnitController.cs
  └── DocumentJobController.cs

Application/
  ├── Interfaces/
  │     ├── Services/           # IAccessControlService, IDocumentService
  │     └── Repository/         # IUnitOfWork, IDatasetRepository, etc.
  ├── Dto/
  └── Service/                  # Application services

Infrastructure/
  ├── DependencyInjection.cs
  ├── ApplicationContext.cs     # EF Core DbContext
  ├── Repositories/            # EF Core repositories
  ├── Services/
  │     ├── AccessControlService.cs   # RBAC logic
  │     ├── GenQAsService.cs
  │     └── RedisCacheService.cs
  └── ExternalServices/         # OCRService, TokenCountService

Models/
  ├── Entities/                 # All EF entities
  ├── Enum/                     # DatasetPermissions, OrganizationRole, etc.
  └── QA/                       # Q&A models
```

---

## Key Patterns

### AccessControlService
Tập trung xử lý RBAC:
```csharp
public interface IAccessControlService
{
    Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, Dataset dataset);
    Task<DatasetPermissions> GetEffectivePermissionsAsync(Guid userId, DatasetItem datasetItem);

    Task<bool> CanViewDatasetAsync(Guid userId, Dataset dataset);
    Task<bool> CanWriteDatasetAsync(Guid userId, Dataset dataset);
    Task<bool> CanDeleteDatasetAsync(Guid userId, Dataset dataset);
    Task<bool> CanShareDatasetAsync(Guid userId, Dataset dataset);

    Task<bool> CanViewDatasetItemAsync(Guid userId, DatasetItem datasetItem);
    Task<bool> CanWriteDatasetItemAsync(Guid userId, DatasetItem datasetItem);

    Task<List<Guid>> GetAccessibleDatasetIdsAsync(Guid userId);
    Task<List<Guid>> GetAccessibleDocumentIdsAsync(Guid userId); // For RAG filtering
}
```

### Keyed LLM Services
```csharp
services.AddKeyedSingleton<ILlmChatCompletion, NvidiaService>(LlmProvider.Nvidia);
services.AddKeyedSingleton<ILlmChatCompletion, VllmService>(LlmProvider.Vllm);
services.AddSingleton<LlmServiceFactory>();
```

### Concurrency Control via Redis Lua
`Infrastructure/LuaScripts/allocate_slots.lua` manages slot allocation per model.

### Local Cache vs S3
`DocumentHelper` manages **local file cache** (not S3):
- `BucketUploads` - original PDF/DOCX files
- `BucketOcr` - OCR output (.md, -summary.txt)
- `BucketQas` - generated Q&A (.json)

### Background Jobs
`GenQaBackgroundJobService` handles Q&A generation:
```
Upload → OCR → Generate Q&A → Summary
```

---

## Important Paths
- **Prompt templates**: `data/prompts/*.md`
- **Cache base dir**: `Utils.FileUtils.CacheBaseDir` (project root, not bin/)
- **EF Core context**: `ApplicationContext` in Infrastructure
- **S3 buckets initialized**: `S3Service.InitializeBucketsAsync()` called in `Program.cs` startup
- **Migrations**: `Migrations/` folder

---

## Development Notes
- `preLaunchTask: build` in `.vscode/launch.json` - launching via VS Code auto-builds
- Serilog logs to `Logs/log-*.json` (rolling daily, 30-day retention)
- `CleanupOldCache()` runs daily via Hangfire recurring job
- File size limit for upload: 100MB
- OCR cache expiration: 24 hours (configurable)

---

## Implementation Workflow

When refactoring or adding new features, ALWAYS follow these steps:

1. **Think Thoroughly** - Spend adequate time analyzing the problem before writing any code
2. **Analyze Requirements** - Break down the requirements, identify all affected components and their responsibilities
3. **Review Architecture** - Examine existing patterns, conventions, and how the feature integrates with current architecture
4. **Plan Implementation** - Determine where changes need to be made (Models, Services, Controllers, etc.)
5. **Implement** - Write code following existing conventions and patterns
6. **Verify** - Run build command to ensure code correctness

NEVER jump straight into coding. Always complete steps 1-3 before proceeding to step 4.
