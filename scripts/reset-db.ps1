# Reset PostgreSQL database in Docker + EF Core migration
# Usage: .\scripts\reset-db.ps1 [-MigrationName "AddNewTable"]
# Usage: .\scripts\reset-db.ps1 -SkipMigration  (only reset DB, no migration)
# Usage: .\scripts\reset-db.ps1 -OnlyUpdate    (only run migration update, no reset DB)

param(
    [string]$MigrationName = "",
    [switch]$SkipMigration,
    [switch]$OnlyUpdate,
    [switch]$CleanMigrations
)

$ErrorActionPreference = "Stop"

$ContainerName = "postgres-prod"
$DbName = "appdb"
$DbUser = "appuser"
$DbPassword = "123456"

# Navigate to project root (parent of scripts/)
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $ProjectRoot

function Reset-Database {
    Write-Host "=== Dropping database with EF Core ===" -ForegroundColor Cyan
    dotnet ef database drop --force

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Failed to drop database." -ForegroundColor Red
        exit 1
    }

    Write-Host "=== Database dropped successfully ===" -ForegroundColor Green
}

function Clean-Migrations {
    # Find all Migrations folders in the project
    $migrationFolders = Get-ChildItem -Path $ProjectRoot -Recurse -Directory -Filter "Migrations" -ErrorAction SilentlyContinue

    if ($migrationFolders) {
        foreach ($folder in $migrationFolders) {
            Write-Host "Deleting migration files in: $($folder.FullName)" -ForegroundColor Yellow
            Remove-Item -Path "$($folder.FullName)\*" -Recurse -Force
        }
        Write-Host "=== All migration files deleted ===" -ForegroundColor Green
    }
    else {
        Write-Host "No Migrations folders found." -ForegroundColor Gray
    }
}

function Add-Migration {
    param([string]$Name)

    if ([string]::IsNullOrEmpty($Name)) {
        Write-Host "Error: Migration name is required." -ForegroundColor Red
        exit 1
    }

    Write-Host "=== Creating migration: '$Name' ===" -ForegroundColor Cyan
    dotnet ef migrations add $Name

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Failed to create migration." -ForegroundColor Red
        exit 1
    }
}

function Update-Database {
    Write-Host "=== Applying migrations to database ===" -ForegroundColor Cyan
    dotnet ef database update

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Failed to update database." -ForegroundColor Red
        exit 1
    }

    Write-Host "=== Database updated successfully ===" -ForegroundColor Green
}

# ── Main ──────────────────────────────────────────────────────────

if ($OnlyUpdate) {
    # Only run migration update (no DB reset)
    Update-Database
}
else {
    # Clean migrations if requested
    if ($CleanMigrations) {
        Clean-Migrations
    }

    # Reset database
    Reset-Database

    if (-not $SkipMigration) {
        # Create migration if name provided
        if (-not [string]::IsNullOrEmpty($MigrationName)) {
            Add-Migration -Name $MigrationName
        }

        # Apply migrations
        Update-Database
    }
}

