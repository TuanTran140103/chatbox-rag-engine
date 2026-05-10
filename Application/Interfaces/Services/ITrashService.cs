using MarkdownGenQAs.Application.Dto.Admin.Trash;
using MarkdownGenQAs.Models;

namespace MarkdownGenQAs.Application.Interfaces.Services;

public interface ITrashService
{
    Task<List<TrashItemDto>> GetTrashItemsAsync(Guid userId);
    Task<ServiceResult> RestoreItemAsync(TrashItemType type, Guid id, Guid userId);
    Task<ServiceResult> EmptyTrashAsync(Guid userId);
    Task<ServiceResult> PermanentDeleteItemAsync(TrashItemType type, Guid id, Guid userId);
}
