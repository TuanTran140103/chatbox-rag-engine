[Flags]
public enum DatasetPermissions
{
    None = 0,
    Read = 1,
    Update = 2,
    Delete = 4,
    Share = 8,

    Collaborate = Read | Update,
    FullControl = Read | Update | Delete | Share
}

public enum OrganizationRole
{
    Staff,
    Manager
}
