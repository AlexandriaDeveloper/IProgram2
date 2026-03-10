IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] nvarchar(450) NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] nvarchar(450) NOT NULL,
        [DisplayName] nvarchar(max) NULL,
        [DisplayImage] nvarchar(max) NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [Daily] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(200) NOT NULL,
        [DailyDate] datetime2 NOT NULL,
        [Closed] bit NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Daily] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [Departments] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(200) NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Departments] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] nvarchar(450) NOT NULL,
        [RoleId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] nvarchar(450) NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [Form] (
        [Id] int NOT NULL IDENTITY,
        [DailyId] int NULL,
        [Index] int NULL,
        [Description] nvarchar(max) NULL,
        [Name] nvarchar(max) NULL,
        [CreatedBy] nvarchar(450) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Form] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Form_AspNetUsers_CreatedBy] FOREIGN KEY ([CreatedBy]) REFERENCES [AspNetUsers] ([Id]),
        CONSTRAINT [FK_Form_Daily_DailyId] FOREIGN KEY ([DailyId]) REFERENCES [Daily] ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [Employees] (
        [Id] nvarchar(14) NOT NULL,
        [TegaraCode] int NULL,
        [TabCode] int NULL,
        [Name] nvarchar(max) NOT NULL,
        [Collage] nvarchar(max) NULL,
        [Section] nvarchar(25) NULL,
        [Email] nvarchar(250) NULL,
        [DepartmentId] int NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Employees] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Employees_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [FormRefernce] (
        [Id] int NOT NULL IDENTITY,
        [FormId] int NOT NULL,
        [ReferencePath] nvarchar(250) NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_FormRefernce] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FormRefernce_Form_FormId] FOREIGN KEY ([FormId]) REFERENCES [Form] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [EmployeeBank] (
        [EmployeeId] nvarchar(14) NOT NULL,
        [BankName] nvarchar(max) NULL,
        [BranchName] nvarchar(max) NULL,
        [AccountNumber] nvarchar(max) NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_EmployeeBank] PRIMARY KEY ([EmployeeId]),
        CONSTRAINT [FK_EmployeeBank_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [EmployeeRefernce] (
        [Id] int NOT NULL IDENTITY,
        [EmployeeId] nvarchar(14) NULL,
        [ReferencePath] nvarchar(max) NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_EmployeeRefernce] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EmployeeRefernce_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE TABLE [FormDetails] (
        [Id] int NOT NULL IDENTITY,
        [FormId] int NOT NULL,
        [EmployeeId] nvarchar(14) NULL,
        [Amount] float NOT NULL,
        [OrderNum] int NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_FormDetails] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FormDetails_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]),
        CONSTRAINT [FK_FormDetails_Form_FormId] FOREIGN KEY ([FormId]) REFERENCES [Form] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_EmployeeRefernce_EmployeeId] ON [EmployeeRefernce] ([EmployeeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_Employees_DepartmentId] ON [Employees] ([DepartmentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Employees_TabCode] ON [Employees] ([TabCode]) WHERE [TabCode] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Employees_TegaraCode] ON [Employees] ([TegaraCode]) WHERE [TegaraCode] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_Form_CreatedBy] ON [Form] ([CreatedBy]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_Form_DailyId] ON [Form] ([DailyId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_FormDetails_EmployeeId] ON [FormDetails] ([EmployeeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_FormDetails_FormId] ON [FormDetails] ([FormId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    CREATE INDEX [IX_FormRefernce_FormId] ON [FormRefernce] ([FormId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240815134033_Init'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20240815134033_Init', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240819221452_Daily-Relation-fix'
)
BEGIN
    ALTER TABLE [Form] DROP CONSTRAINT [FK_Form_Daily_DailyId];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240819221452_Daily-Relation-fix'
)
BEGIN
    ALTER TABLE [Form] ADD CONSTRAINT [FK_Form_Daily_DailyId] FOREIGN KEY ([DailyId]) REFERENCES [Daily] ([Id]) ON DELETE CASCADE;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240819221452_Daily-Relation-fix'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20240819221452_Daily-Relation-fix', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240819222322_Department-Relation-fix'
)
BEGIN
    ALTER TABLE [Employees] DROP CONSTRAINT [FK_Employees_Departments_DepartmentId];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240819222322_Department-Relation-fix'
)
BEGIN
    ALTER TABLE [Employees] ADD CONSTRAINT [FK_Employees_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([Id]) ON DELETE SET NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240819222322_Department-Relation-fix'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20240819222322_Department-Relation-fix', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250815185021_Add-Review-Field'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [IsReviewed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250815185021_Add-Review-Field'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [IsReviewedBy] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250815185021_Add-Review-Field'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [ReviewComments] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250815185021_Add-Review-Field'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [ReviewedAt] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250815185021_Add-Review-Field'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250815185021_Add-Review-Field', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250907211723_Add-DailyReference'
)
BEGIN
    CREATE TABLE [DailyReference] (
        [Id] int NOT NULL IDENTITY,
        [DailyId] int NOT NULL,
        [ReferencePath] nvarchar(max) NOT NULL,
        [Description] nvarchar(max) NULL,
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetime2 NULL,
        [DeactivatedBy] nvarchar(max) NULL,
        [DeactivatedAt] datetime2 NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_DailyReference] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DailyReference_Daily_DailyId] FOREIGN KEY ([DailyId]) REFERENCES [Daily] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250907211723_Add-DailyReference'
)
BEGIN
    CREATE INDEX [IX_DailyReference_DailyId] ON [DailyReference] ([DailyId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250907211723_Add-DailyReference'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250907211723_Add-DailyReference', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260227150824_AddSummaryReviewFields'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [IsSummaryReviewed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260227150824_AddSummaryReviewFields'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [IsSummaryReviewedBy] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260227150824_AddSummaryReviewFields'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [SummaryReviewedAt] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260227150824_AddSummaryReviewFields'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260227150824_AddSummaryReviewFields', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260227162856_AddSummaryComment'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [SummaryComments] nvarchar(max) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260227162856_AddSummaryComment'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260227162856_AddSummaryComment', N'8.0.0');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260310112020_AddFormDetailsFormIdIndex'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260310112020_AddFormDetailsFormIdIndex', N'8.0.0');
END;
GO

COMMIT;
GO

