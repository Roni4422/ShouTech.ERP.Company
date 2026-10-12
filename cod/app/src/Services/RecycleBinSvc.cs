using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services;

/// <summary>
/// Soft-deleted master-data row exposed by the company recycle bin.
/// Table and column names are always resolved from a static whitelist — never from user input.
/// </summary>
public sealed class RecycleBinItem
{
    public required string TableName { get; init; }
    public required string DatasetKey { get; init; }
    public required string PrimaryKeyColumn { get; init; }
    public required string PrimaryKeyValue { get; init; }
    public required string DisplayLabel { get; init; }
    public string? DeletedAt { get; init; }
    public string? DeletedBy { get; init; }
    public required string CompanyCode { get; init; }
}

/// <summary>Outcome of a restore or purge operation for a single bin item.</summary>
public sealed class RecycleBinMutationResult
{
    public required RecycleBinItem Item { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Company-scoped recycle bin for soft-deleted master data (<c>is_deleted = 1</c>).
/// Supports list, restore, and permanent purge. Identifiers are restricted to a fixed catalog.
/// </summary>
public static class RecycleBinSvc
{
    private const string LogCategory = "RecycleBin";
    private const int MaxRowsPerTable = 500;
    private static readonly Regex SafeIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private sealed record BinTableDefinition(
        string TableName,
        string DatasetKey,
        string[] LabelColumnCandidates,
        string[] PrimaryKeyCandidates,
        bool CompanyIdIsCode = false,
        bool DraftOnly = false);

    /// <summary>Master-data tables only. Transactional tables are intentionally excluded.</summary>
    private static readonly BinTableDefinition[] Catalog =
    [
        new("sales_documents", "Sales invoices",
            ["doc_no", "party_name", "doc_id"],
            ["doc_id"], CompanyIdIsCode: true, DraftOnly: true),
        new("purchase_documents", "Purchase invoices",
            ["doc_no", "party_name", "doc_id"],
            ["doc_id"], CompanyIdIsCode: true, DraftOnly: true),
        new("receipt_vouchers", "Receipt vouchers",
            ["voucher_no", "doc_no", "number", "description", "id"],
            ["voucher_id", "receipt_voucher_id", "id"]),
        new("payment_vouchers", "Payment vouchers",
            ["voucher_no", "doc_no", "number", "description", "id"],
            ["voucher_id", "payment_voucher_id", "id"]),
        new("products", "Products",
            ["product_name_ar", "product_name_en", "product_name", "name", "code", "barcode"],
            ["product_id", "id"]),
        new("materials", "Products",
            ["name_ar", "name_en", "name", "code", "barcode"],
            ["material_id", "id"]),
        new("customers", "Customers",
            ["customer_name_ar", "customer_name", "name", "code"],
            ["customer_id", "id"]),
        new("suppliers", "Suppliers",
            ["supplier_name_ar", "supplier_name", "name", "code"],
            ["supplier_id", "id"]),
        new("accounts", "Accounts",
            ["account_name_ar", "account_name", "name", "code", "account_code"],
            ["account_id", "id"]),
        new("chart_of_accounts", "Accounts",
            ["account_name_ar", "account_name", "name", "code"],
            ["account_id", "id"]),
        new("branches", "Branches",
            ["branch_name_ar", "branch_name", "name", "code"],
            ["branch_id", "id"]),
        new("warehouses", "Warehouses",
            ["warehouse_name_ar", "name", "code"],
            ["warehouse_id", "id"]),
        new("brands", "Brands",
            ["brand_name_ar", "name", "code"],
            ["brand_id", "id"]),
        new("product_categories", "ProductClasses",
            ["category_name_ar", "name", "code"],
            ["category_id", "id"]),
        new("units_of_measure", "Units",
            ["unit_name_ar", "name", "uom_code", "code"],
            ["uom_id", "id"]),
        new("employees", "Employees",
            ["employee_name_ar", "name", "code"],
            ["employee_id", "id"]),
    ];

    public static async Task<IReadOnlyList<RecycleBinItem>> ListAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            return Array.Empty<RecycleBinItem>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var existingTables = await GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var companyId = await FindCompanyIdAsync(connection, companyCode, cancellationToken).ConfigureAwait(false);
        var results = new List<RecycleBinItem>();

        var definitions = Catalog
            .Where(definition => existingTables.Contains(definition.TableName))
            .ToDictionary(definition => definition.TableName, StringComparer.OrdinalIgnoreCase);

        // Automatically discover additional company-scoped tables using the standard soft-delete flag.
        foreach (var tableName in existingTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definitions.ContainsKey(tableName) || !SafeIdentifier.IsMatch(tableName))
                continue;
            try
            {
                var columns = await GetColumnNamesAsync(connection, tableName, cancellationToken).ConfigureAwait(false);
                if (FindColumn(columns, "is_deleted") is null)
                    continue;
                var primaryKey = InferPrimaryKey(tableName, columns);
                if (primaryKey is null)
                    continue;
                var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
                if (companyColumn is null)
                    continue;

                var isInvoice = tableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                    || tableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase);
                definitions[tableName] = new BinTableDefinition(
                    tableName,
                    HumanizeTableName(tableName),
                    ["name_ar", "name", "description", "doc_no", "voucher_no", "code", "number", "title", "id"],
                    [primaryKey],
                    CompanyIdIsCode: isInvoice,
                    DraftOnly: isInvoice);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarning($"Dynamic discovery skipped table '{tableName}': {ex.Message}");
            }
        }

        foreach (var definition in definitions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = await ListTableAsync(
                    connection, definition, companyCode, companyId, cancellationToken).ConfigureAwait(false);
                results.AddRange(page);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarning($"List skipped for table '{definition.TableName}': {ex.Message}");
            }
        }

        return results
            .OrderByDescending(item => item.DeletedAt ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.DatasetKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static async Task<int> CountAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(companyCode, cancellationToken).ConfigureAwait(false);
        return items.Count;
    }

    public static async Task<RecycleBinMutationResult> RestoreAsync(
        RecycleBinItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateCatalogItem(item);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);

            var columns = await GetColumnNamesAsync(connection, item.TableName, cancellationToken)
                .ConfigureAwait(false);
            var softDeleteColumn = FindColumn(columns, "is_deleted");
            if (softDeleteColumn is null)
                return Failed(item, "This table does not support soft-delete restore.");

            var deletedAtColumn = FindColumn(columns, "deleted_at");
            var isActiveColumn = FindColumn(columns, "is_active");
            var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
            var definition = Catalog.FirstOrDefault(entry => entry.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase))
                ?? new BinTableDefinition(item.TableName, HumanizeTableName(item.TableName), [], [item.PrimaryKeyColumn],
                    CompanyIdIsCode: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase),
                    DraftOnly: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase));
            var statusColumn = definition.DraftOnly ? FindColumn(columns, "status") : null;
            if (definition.DraftOnly && statusColumn is null)
                return Failed(item, "Draft status is unavailable; this document cannot be restored from the bin.");
            var companyValue = definition.CompanyIdIsCode
                ? item.CompanyCode
                : await FindCompanyIdAsync(connection, item.CompanyCode, cancellationToken).ConfigureAwait(false);
            var primaryKeyColumn = ResolveExistingColumn(columns, item.PrimaryKeyColumn);

            await using var command = connection.CreateCommand();
            var assignments = new List<string> { $"{Quote(softDeleteColumn)} = 0" };
            if (deletedAtColumn is not null)
                assignments.Add($"{Quote(deletedAtColumn)} = NULL");
            if (isActiveColumn is not null)
                assignments.Add($"{Quote(isActiveColumn)} = 1");

            var predicates = new List<string>
            {
                $"{Quote(primaryKeyColumn)} = @primaryKey",
                $"{Quote(softDeleteColumn)} = 1"
            };
            if (companyColumn is not null)
            {
                if (companyValue is null)
                    return Failed(item, "Company could not be resolved; restore was blocked.");
                predicates.Add($"{Quote(companyColumn)} = @companyValue");
                AddParameter(command, "@companyValue", companyValue);
            }
            if (definition.DraftOnly)
            {
                predicates.Add($"{Quote(statusColumn!)} = @draftStatus");
                AddParameter(command, "@draftStatus", "Draft");
            }
            command.CommandText =
                $"UPDATE {Quote(item.TableName)} SET {string.Join(", ", assignments)} " +
                $"WHERE {string.Join(" AND ", predicates)}";

            AddParameter(command, "@primaryKey", CoercePrimaryKey(item.PrimaryKeyValue));

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected <= 0)
                return Failed(item, "No matching soft-deleted row was found.");

            await WriteAuditEventAsync(connection, item, "RESTORE", Environment.UserName,
                "Restore from recycle bin", cancellationToken).ConfigureAwait(false);
            LogOperation("Restore",
                $"table={item.TableName} pk={item.PrimaryKeyValue} label={item.DisplayLabel} company={item.CompanyCode}");

            return new RecycleBinMutationResult { Item = item, Succeeded = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($"Restore failed | table={item.TableName} | pk={item.PrimaryKeyValue} | {ex.Message}", ex);
            return Failed(item, ex.Message);
        }
    }

    public static async Task<IReadOnlyList<RecycleBinMutationResult>> RestoreManyAsync(
        IEnumerable<RecycleBinItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<RecycleBinMutationResult>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RestoreAsync(item, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public static async Task<RecycleBinMutationResult> PurgeAsync(
        RecycleBinItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateCatalogItem(item);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);

            var columns = await GetColumnNamesAsync(connection, item.TableName, cancellationToken)
                .ConfigureAwait(false);
            var softDeleteColumn = FindColumn(columns, "is_deleted");
            var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
            var definition = Catalog.FirstOrDefault(entry => entry.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase))
                ?? new BinTableDefinition(item.TableName, HumanizeTableName(item.TableName), [], [item.PrimaryKeyColumn],
                    CompanyIdIsCode: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase),
                    DraftOnly: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase));
            var statusColumn = definition.DraftOnly ? FindColumn(columns, "status") : null;
            if (definition.DraftOnly && statusColumn is null)
                return Failed(item, "Draft status is unavailable; permanent deletion was blocked.");
            var companyValue = definition.CompanyIdIsCode
                ? item.CompanyCode
                : await FindCompanyIdAsync(connection, item.CompanyCode, cancellationToken).ConfigureAwait(false);
            var primaryKeyColumn = ResolveExistingColumn(columns, item.PrimaryKeyColumn);

            await using var command = connection.CreateCommand();
            var predicates = new List<string> { $"{Quote(primaryKeyColumn)} = @primaryKey" };
            if (softDeleteColumn is not null)
                predicates.Add($"{Quote(softDeleteColumn)} = 1");
            if (companyColumn is not null)
            {
                if (companyValue is null)
                    return Failed(item, "Company could not be resolved; permanent deletion was blocked.");
                predicates.Add($"{Quote(companyColumn)} = @companyValue");
                AddParameter(command, "@companyValue", companyValue);
            }
            if (definition.DraftOnly)
            {
                predicates.Add($"{Quote(statusColumn!)} = @draftStatus");
                AddParameter(command, "@draftStatus", "Draft");
            }
            command.CommandText =
                $"DELETE FROM {Quote(item.TableName)} WHERE {string.Join(" AND ", predicates)}";

            AddParameter(command, "@primaryKey", CoercePrimaryKey(item.PrimaryKeyValue));

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected <= 0)
                return Failed(item, "No matching bin row was found to purge.");

            await WriteAuditEventAsync(connection, item, "PERMANENT_DELETE", Environment.UserName,
                "Permanent deletion from recycle bin", cancellationToken).ConfigureAwait(false);
            LogOperation("Purge",
                $"table={item.TableName} pk={item.PrimaryKeyValue} label={item.DisplayLabel} company={item.CompanyCode}");

            return new RecycleBinMutationResult { Item = item, Succeeded = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($"Purge failed | table={item.TableName} | pk={item.PrimaryKeyValue} | {ex.Message}", ex);
            return Failed(item, ex.Message);
        }
    }

    private static async Task<IReadOnlyList<RecycleBinItem>> ListTableAsync(
        DbConnection connection,
        BinTableDefinition definition,
        string companyCode,
        object? companyId,
        CancellationToken cancellationToken)
    {
        var columns = await GetColumnNamesAsync(connection, definition.TableName, cancellationToken)
            .ConfigureAwait(false);

        var softDeleteColumn = FindColumn(columns, "is_deleted");
        if (softDeleteColumn is null)
            return Array.Empty<RecycleBinItem>();

        var primaryKeyColumn = definition.PrimaryKeyCandidates
            .Select(candidate => FindColumn(columns, candidate))
            .FirstOrDefault(column => column is not null);
        if (primaryKeyColumn is null)
            return Array.Empty<RecycleBinItem>();

        var labelColumn = definition.LabelColumnCandidates
            .Select(candidate => FindColumn(columns, candidate))
            .FirstOrDefault(column => column is not null) ?? primaryKeyColumn;

        var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
        var deletedAtColumn = FindColumn(columns, "deleted_at");
        var deletedByColumn = FindColumn(columns, "deleted_by");

        await using var command = connection.CreateCommand();
        var selectList = new List<string> { Quote(primaryKeyColumn), Quote(labelColumn) };
        if (deletedAtColumn is not null)
            selectList.Add(Quote(deletedAtColumn));
        if (deletedByColumn is not null)
            selectList.Add(Quote(deletedByColumn));

        var predicates = new List<string> { $"{Quote(softDeleteColumn)} = 1" };
        if (companyColumn is not null)
        {
            var companyValue = definition.CompanyIdIsCode ? companyCode : companyId;
            if (companyValue is null)
                return Array.Empty<RecycleBinItem>();
            predicates.Add($"{Quote(companyColumn)} = @companyId");
            AddParameter(command, "@companyId", companyValue);
        }
        if (definition.DraftOnly)
        {
            var statusColumn = FindColumn(columns, "status");
            if (statusColumn is null)
                return Array.Empty<RecycleBinItem>();
            predicates.Add($"{Quote(statusColumn)} = @draftStatus");
            AddParameter(command, "@draftStatus", "Draft");
        }

        command.CommandText =
            $"SELECT {string.Join(", ", selectList)} " +
            $"FROM {Quote(definition.TableName)} " +
            $"WHERE {string.Join(" AND ", predicates)} " +
            $"ORDER BY 1 DESC " +
            $"LIMIT {MaxRowsPerTable}";

        var items = new List<RecycleBinItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var primaryKeyValue = ReadString(reader, 0) ?? string.Empty;
            var label = ReadString(reader, 1);
            if (string.IsNullOrWhiteSpace(label))
                label = primaryKeyValue;

            var ordinal = 2;
            string? deletedAt = null;
            string? deletedBy = null;
            if (deletedAtColumn is not null)
            {
                deletedAt = ReadString(reader, ordinal);
                ordinal++;
            }
            if (deletedByColumn is not null)
                deletedBy = ReadString(reader, ordinal);

            items.Add(new RecycleBinItem
            {
                TableName = definition.TableName,
                DatasetKey = definition.DatasetKey,
                PrimaryKeyColumn = primaryKeyColumn,
                PrimaryKeyValue = primaryKeyValue,
                DisplayLabel = label!,
                DeletedAt = deletedAt,
                DeletedBy = deletedBy,
                CompanyCode = companyCode
            });
        }

        return items;
    }

    /// <summary>Durable audit hook for application delete handlers.</summary>
    public static async Task RecordDeletionAsync(
        string companyCode, string tableName, string recordKey, string actor,
        string method, string? reason, string? recordSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode) || !SafeIdentifier.IsMatch(tableName))
            throw new ArgumentException("A company and valid table name are required.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO deletion_audit_log " +
            "(event_id, company_code, table_name, record_key, action_type, actor, occurred_at, method, reason, record_snapshot) " +
            "VALUES (@eventId, @company, @table, @recordKey, 'DELETE', @actor, @occurredAt, @method, @reason, @snapshot)";
        AddParameter(command, "@eventId", Guid.NewGuid().ToString("D"));
        AddParameter(command, "@company", companyCode);
        AddParameter(command, "@table", tableName);
        AddParameter(command, "@recordKey", recordKey);
        AddParameter(command, "@actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor);
        AddParameter(command, "@occurredAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@method", string.IsNullOrWhiteSpace(method) ? "SOFT_DELETE" : method);
        AddParameter(command, "@reason", (object?)reason ?? DBNull.Value);
        AddParameter(command, "@snapshot", (object?)recordSnapshot ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDeletionAuditTableAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS deletion_audit_log (" +
            "event_id TEXT PRIMARY KEY, company_code TEXT NOT NULL, table_name TEXT NOT NULL, " +
            "record_key TEXT NOT NULL, action_type TEXT NOT NULL, actor TEXT NOT NULL, " +
            "occurred_at TEXT NOT NULL, method TEXT NOT NULL, reason TEXT, record_snapshot TEXT)";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAuditEventAsync(
        DbConnection connection, RecycleBinItem item, string action, string actor,
        string details, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO deletion_audit_log " +
            "(event_id, company_code, table_name, record_key, action_type, actor, occurred_at, method, reason, record_snapshot) " +
            "VALUES (@eventId, @company, @table, @recordKey, @action, @actor, @occurredAt, @method, @reason, @snapshot)";
        AddParameter(command, "@eventId", Guid.NewGuid().ToString("D"));
        AddParameter(command, "@company", item.CompanyCode);
        AddParameter(command, "@table", item.TableName);
        AddParameter(command, "@recordKey", item.PrimaryKeyValue);
        AddParameter(command, "@action", action);
        AddParameter(command, "@actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor);
        AddParameter(command, "@occurredAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@method", action);
        AddParameter(command, "@reason", details);
        AddParameter(command, "@snapshot", JsonSerializer.Serialize(new
        {
            item.DatasetKey, item.DisplayLabel, item.DeletedAt, item.DeletedBy
        }));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCatalogItem(RecycleBinItem item)
    {
        if (!SafeIdentifier.IsMatch(item.TableName) || !SafeIdentifier.IsMatch(item.PrimaryKeyColumn))
            throw new InvalidOperationException("Invalid identifier in recycle bin item.");
    }

    private static string? InferPrimaryKey(string tableName, IReadOnlyList<string> columns)
    {
        var singular = tableName.EndsWith("ies", StringComparison.OrdinalIgnoreCase)
            ? tableName[..^3] + "y"
            : tableName.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? tableName[..^1] : tableName;
        foreach (var candidate in new[] { singular + "_id", tableName.TrimEnd('_') + "_id", "id", "doc_id", "voucher_id" })
        {
            var found = FindColumn(columns, candidate);
            if (found is not null)
                return found;
        }
        return columns.FirstOrDefault(column => column.EndsWith("_id", StringComparison.OrdinalIgnoreCase));
    }

    private static string HumanizeTableName(string tableName)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(tableName, "_+", " ").Trim());

    private static string ResolveExistingColumn(IReadOnlyList<string> columns, string requested)
    {
        var match = FindColumn(columns, requested);
        if (match is null)
            throw new InvalidOperationException($"Column '{requested}' was not found.");
        return match;
    }

    private static string? FindColumn(IReadOnlyList<string> columns, string name)
        => columns.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static RecycleBinMutationResult Failed(RecycleBinItem item, string message)
        => new() { Item = item, Succeeded = false, ErrorMessage = message };

    private static object CoercePrimaryKey(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64))
            return int64;
        if (Guid.TryParse(value, out var guid))
            return guid.ToString("D", CultureInfo.InvariantCulture);
        return value;
    }

    private static string? ReadString(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(string identifier)
    {
        if (!SafeIdentifier.IsMatch(identifier))
            throw new ArgumentException($"Unsafe SQL identifier: '{identifier}'.", nameof(identifier));
        return $"\"{identifier}\"";
    }

    private static DbConnection CreateConnection()
    {
        var connectionString = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("Database connection is not configured.");
        if (DatabaseRuntime.IsPostgreSql())
            return new NpgsqlConnection(connectionString);
        return new SqliteConnection(connectionString);
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"
            : "SELECT name FROM sqlite_master WHERE type = 'table'";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<IReadOnlyList<string>> GetColumnNamesAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        if (!SafeIdentifier.IsMatch(tableName))
            throw new ArgumentException($"Unsafe table name: '{tableName}'.", nameof(tableName));

        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        if (DatabaseRuntime.IsPostgreSql())
        {
            command.CommandText =
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @tableName";
            AddParameter(command, "@tableName", tableName);
        }
        else
        {
            command.CommandText = $"PRAGMA table_info({Quote(tableName)})";
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(DatabaseRuntime.IsPostgreSql()
                ? reader.GetString(0)
                : reader.GetString(1));
        }

        return columns;
    }

    private static async Task<object?> FindCompanyIdAsync(
        DbConnection connection,
        string companyCode,
        CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "SELECT company_id FROM companies WHERE company_code = @code LIMIT 1",
            "SELECT id FROM companies WHERE code = @code LIMIT 1",
        })
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                AddParameter(command, "@code", companyCode);
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (scalar is not null and not DBNull)
                    return scalar;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return null;
    }

    private static void LogOperation(string action, string message)
    {
        try { AppServiceHost.Logging.LogOperation(LogCategory, action, message); }
        catch { }
    }

    private static void LogWarning(string message)
    {
        try { AppServiceHost.Logging.LogWarning(LogCategory, message); }
        catch { }
    }

    private static void LogError(string message, Exception exception)
    {
        try { AppServiceHost.Logging.LogError(LogCategory, message, exception); }
        catch { }
    }
}
