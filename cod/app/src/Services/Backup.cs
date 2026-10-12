// ========================================================================
// FILE: Services/Backup.cs
// PROJECT: SHOUTECH ERP — Backup service + STEI encrypted packages
// Merged: BkpFileInfo, IBkpSvc, SteiModels, SteiCrypto, SteiSvc, BkpServ
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data.Common;
using System.Data;
using System.IO.Compression;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Configuration;
using System.Globalization;

namespace ShouTech.App.Services
{

    // ========== BkpFileInfo.cs ==========
/// <summary>
/// Presentation-neutral information about a local ShouTech backup package.
/// </summary>
public sealed class BkpFileInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTime LastWriteUtc { get; init; }
    public long SizeBytes { get; init; }
    public bool IsValid { get; set; }
    public string Status { get; set; } = string.Empty;
}

    // ========== IBkpSvc.cs ==========
public class BackupResult
    {
        public bool Success { get; set; }
        public string? LocalPath { get; set; }
        public bool Uploaded { get; set; }
        public string? Message { get; set; }
    }

        public class BackupValidationDetail
    {
        public bool IsValid { get; set; }
        public string? FailureReason { get; set; }
    }

    public interface IBkpServ
    {
        Task<string> CreateBackupAsync(string companyCode, CancellationToken ct = default);
	Task<string> CreateBackupAsync(string companyCode, string notes);
        Task RestoreBackupAsync(string backupFilePath, CancellationToken ct = default);
        Task<bool> ValidateBackupAsync(string backupFilePath, CancellationToken ct = default);
        void PerformBackup();
        BackupResult PerformRotatedBackup();
        bool IsBackupDue();
        Task<BackupResult> RunOnlineBackupCycleAsync(bool force = false, CancellationToken ct = default);
        Task<bool> TryUploadRotatedBackupsAsync(CancellationToken ct = default);

        List<BkpFileInfo> ListBackups(string companyCode);
        Task<BackupValidationDetail> ValidateBackupDetailedAsync(string backupFilePath, CancellationToken ct = default);
        string GetBackupDirectory(string companyCode);
    }

    // ========== SteiModels.cs ==========
internal sealed class SteiManifest
{
    public string Format { get; set; } = "SHOUTECH-STEI";
    public int FormatVersion { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public string PackageId { get; set; } = Guid.NewGuid().ToString("D");
    public string ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string ExportedLocal { get; set; } = DateTimeOffset.Now.ToString("O");
    public string ExportedByUserId { get; set; } = "";
    public string ExportedByUserName { get; set; } = "";
    public string SourceCompanyId { get; set; } = "";
    public string SourceCompanyCode { get; set; } = "";
    public string SourceCompanyName { get; set; } = "";
    public string SourceDatabaseName { get; set; } = "";
    public string SourceDatabaseProvider { get; set; } = "";
    public string TargetDatabaseName { get; set; } = "";
    public string TargetDatabaseProvider { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public string DataScope { get; set; } = "CompanyData";
    public string OperationId { get; set; } = "";
    public string OperationType { get; set; } = "Export";
    public string DatasetKey { get; set; } = "";
    public string DatasetTableName { get; set; } = "";
    public List<string> DatasetFields { get; set; } = new();
    public Dictionary<string, string> DatasetFieldTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> TableFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int TableCount { get; set; }
    public long RowCount { get; set; }
    public string PayloadHash { get; set; } = "";
    public string SignatureAlgorithm { get; set; } = "RSA-PSS-SHA256";
    public string Signature { get; set; } = "";
    public string SigningKeyFingerprint { get; set; } = "";
    public string EncryptionAlgorithm { get; set; } = "AES-256-GCM";
    public string KeyDerivation { get; set; } = "PBKDF2-SHA256";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 310_000;
    public string Nonce { get; set; } = "";
    public List<string> ExcludedTables { get; set; } = new();
}

internal sealed class SteiPackage
{
    public SteiManifest Manifest { get; set; } = new();
    public Dictionary<string, List<Dictionary<string, JsonElement>>> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record SteiResult(bool Success, bool Partial, long Rows, long FailedRows, int Tables, int FailedTables, IReadOnlyList<string> Errors, string Message, long DeletedRows = 0);

public sealed record SteiPackageInfo(
    string FileName, string PackageId, string OperationId, string OperationType,
    string DataScope, string DatasetKey, string DatasetTableName, IReadOnlyList<string> Fields,
    IReadOnlyDictionary<string, string> FieldTypes, string ExportedAtLocal, string ExportedByUserId,
    string ExportedByUserName, string SourceCompanyId, string SourceCompanyCode, string SourceCompanyName,
    string SourceDatabaseName, string SourceDatabaseProvider, string ApplicationVersion,
    int FormatVersion, int SchemaVersion, int TableCount, long RowCount, string PayloadHash);

    // ========== SteiCrypto.cs ==========
internal static class SteiCrypto
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int Iterations = 310_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static (byte[] Cipher, byte[] Salt, byte[] Nonce, byte[] Tag) Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, Iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return (cipher, salt, nonce, tag);
    }

    public static byte[] Decrypt(byte[] cipher, string password, byte[] salt, byte[] nonce, byte[] tag)
    {
        var key = DeriveKey(password, salt, Iterations);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    public static RSA LoadSigningKey()
    {
        var dir = AppPaths.Security;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "stei-signing.key");
        var rsa = RSA.Create(3072);
        if (File.Exists(path))
        {
            var protectedBlob = File.ReadAllBytes(path);
            var blob = ProtectedData.Unprotect(protectedBlob, null, DataProtectionScope.CurrentUser);
            rsa.ImportPkcs8PrivateKey(blob, out _);
            CryptographicOperations.ZeroMemory(blob);
            return rsa;
        }

        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var protectedKey = ProtectedData.Protect(pkcs8, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedKey);
        CryptographicOperations.ZeroMemory(pkcs8);
        return rsa;
    }

    public static byte[] ExportPublicKey(RSA rsa) => rsa.ExportSubjectPublicKeyInfo();

    public static byte[] Sign(RSA rsa, byte[] data)
        => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public static string Fingerprint(byte[] publicKey)
        => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

    public static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
}

    // ========== SteiSvc.cs ==========
internal static class SteiSvc
{
    private const string Magic = "SHOUTECH-STEI\n";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqlite_sequence", "companies", "users", "roles", "permissions", "user_roles", "role_permissions",
        "permission_groups", "user_permissions", "journal_audit_events"
    };

    public static async Task<SteiResult> ExportAsync(string outputPath, string password, IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.", nameof(outputPath));

        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString() ?? throw new InvalidOperationException("The current database connection is not configured.");
        await using var db = CreateConnection(provider, cs);
        await db.OpenAsync(ct);

        var manifest = BuildManifest(provider);
        manifest.OperationType = "FullDatabaseExport";
        var package = new SteiPackage { Manifest = manifest };
        var tables = await GetTablesAsync(db, provider, ct);
        var total = 0L;
        var errors = new List<string>();

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded.Contains(table))
                continue;
            var columns = await GetColumnsAsync(db, provider, table, ct);
            if (columns.Count > 0)
                manifest.TableFields[table] = columns.ToList();
            if (columns.Count == 0)
                continue;
            var rows = await ReadRowsAsync(db, provider, table, columns, progress, total, ct);
            if (rows.Count == 0)
                continue;
            package.Tables[table] = rows;
            total += rows.Count;
        }

        manifest.TableCount = package.Tables.Count;
        manifest.RowCount = total;
        manifest.ExcludedTables = Excluded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var payload = SteiCrypto.Serialize(package.Tables);
        manifest.PayloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var signingKey = SteiCrypto.LoadSigningKey();
        var publicKey = SteiCrypto.ExportPublicKey(signingKey);
        manifest.SigningKeyFingerprint = SteiCrypto.Fingerprint(publicKey);

        // Encryption metadata is part of the signed manifest. Populate it first.
        var encrypted = SteiCrypto.Encrypt(payload, password);
        manifest.Salt = Convert.ToBase64String(encrypted.Salt);
        manifest.Nonce = Convert.ToBase64String(encrypted.Nonce);
        manifest.Iterations = 310_000;

        var signatureData = BuildSignatureData(manifest, payload);
        manifest.Signature = Convert.ToBase64String(SteiCrypto.Sign(signingKey, signatureData));

        if (package.Tables.Count == 0)
        {
            errors.Add("No table rows were exported. Check that the database contains data and that the active company matches company_id values.");
            return new SteiResult(false, false, 0, 0, 0, 0, errors, "Export produced an empty package.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await WriteContainerAsync(outputPath, manifest, encrypted.Cipher, encrypted.Tag, publicKey, ct);
        progress?.Report((total, total));
        return new SteiResult(true, false, total, 0, package.Tables.Count, 0, errors, "Export completed successfully.");
    }

    public static async Task<SteiResult> ExportDatasetAsync(
        string outputPath,
        string password,
        string datasetKey,
        string tableName,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> fields,
        IReadOnlyDictionary<string, string>? fieldTypes = null,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        if (string.IsNullOrWhiteSpace(datasetKey) || string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("A dataset key and table name are required.");
        if (rows.Count == 0 || fields.Count == 0)
            throw new InvalidOperationException("The selected dataset has no exportable records.");

        var provider = DatabaseRuntime.GetProvider();
        var manifest = BuildManifest(provider);
        manifest.SchemaVersion = 2;
        manifest.DataScope = $"Dataset:{datasetKey}";
        manifest.OperationType = "DatasetExport";
        manifest.DatasetKey = datasetKey;
        manifest.DatasetTableName = tableName;
        manifest.DatasetFields = fields.ToList();
        manifest.DatasetFieldTypes = fieldTypes is null
            ? fields.ToDictionary(field => field, _ => "Unknown", StringComparer.OrdinalIgnoreCase)
            : fields.ToDictionary(field => field, field => fieldTypes.TryGetValue(field, out var type) ? type : "Unknown", StringComparer.OrdinalIgnoreCase);
        var tableRows = rows.Select(row => fields.ToDictionary(
            field => field,
            field => ToJsonElement(row.TryGetValue(field, out var value) ? value : null),
            StringComparer.OrdinalIgnoreCase)).ToList();
        var tables = new Dictionary<string, List<Dictionary<string, JsonElement>>>(StringComparer.OrdinalIgnoreCase)
        {
            [tableName] = tableRows
        };
        manifest.TableCount = 1;
        manifest.RowCount = tableRows.Count;
        var payload = SteiCrypto.Serialize(tables);
        manifest.PayloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var signingKey = SteiCrypto.LoadSigningKey();
        var publicKey = SteiCrypto.ExportPublicKey(signingKey);
        manifest.SigningKeyFingerprint = SteiCrypto.Fingerprint(publicKey);
        var encrypted = SteiCrypto.Encrypt(payload, password);
        manifest.Salt = Convert.ToBase64String(encrypted.Salt);
        manifest.Nonce = Convert.ToBase64String(encrypted.Nonce);
        manifest.Iterations = 310_000;
        manifest.Signature = Convert.ToBase64String(
            SteiCrypto.Sign(signingKey, BuildSignatureData(manifest, payload)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await WriteContainerAsync(outputPath, manifest, encrypted.Cipher, encrypted.Tag, publicKey, ct);
        progress?.Report((tableRows.Count, tableRows.Count));
        return new SteiResult(true, false, tableRows.Count, 0, 1, 0, Array.Empty<string>(), "Dataset export completed successfully.");
    }

    public static async Task<SteiPackageInfo> ReadDatasetPackageInfoAsync(
        string inputPath, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        var container = await ReadContainerAsync(inputPath, ct);
        var manifest = container.Manifest;
        ValidateManifest(manifest);
        var fingerprint = SteiCrypto.Fingerprint(container.PublicKey);
        if (!string.Equals(fingerprint, manifest.SigningKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The STEI signing key fingerprint does not match the manifest.");
        var payload = SteiCrypto.Decrypt(container.Cipher, password,
            Convert.FromBase64String(manifest.Salt), Convert.FromBase64String(manifest.Nonce), container.Tag);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(manifest.PayloadHash)))
            throw new CryptographicException("The STEI payload hash does not match the manifest.");
        if (!SteiCrypto.Verify(container.PublicKey, BuildSignatureData(manifest, payload), Convert.FromBase64String(manifest.Signature)))
            throw new CryptographicException("The STEI digital signature is invalid.");
        var tables = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(payload)
            ?? throw new InvalidDataException("The STEI payload is empty or invalid.");
        var fields = manifest.DatasetFields.Count > 0
            ? manifest.DatasetFields
            : tables.Count == 1 ? tables.Values.First().FirstOrDefault()?.Keys.ToList() ?? new List<string>() : new List<string>();
        return new SteiPackageInfo(Path.GetFileName(inputPath), manifest.PackageId, manifest.OperationId,
            manifest.OperationType, manifest.DataScope, manifest.DatasetKey, manifest.DatasetTableName,
            fields, manifest.DatasetFieldTypes, manifest.ExportedLocal, manifest.ExportedByUserId,
            manifest.ExportedByUserName, manifest.SourceCompanyId, manifest.SourceCompanyCode,
            manifest.SourceCompanyName, manifest.SourceDatabaseName, manifest.SourceDatabaseProvider,
            manifest.ApplicationVersion, manifest.FormatVersion, manifest.SchemaVersion,
            manifest.TableCount, manifest.RowCount, manifest.PayloadHash);
    }

    public static Task<SteiResult> ImportDatasetAsync(
        string inputPath,
        string password,
        string datasetKey,
        string tableName,
        bool simulation = false,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default,
        bool replaceMode = false)
        => ImportCoreAsync(inputPath, password, simulation, datasetKey, tableName, progress, ct, replaceMode);

    public static Task<SteiResult> ImportAsync(
        string inputPath,
        string password,
        bool simulation,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
        => ImportCoreAsync(inputPath, password, simulation, null, null, progress, ct);

    private static async Task<SteiResult> ImportCoreAsync(
        string inputPath,
        string password,
        bool simulation,
        string? expectedDatasetKey,
        string? expectedTableName,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default,
        bool replaceDatasetMode = false)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A package password is required.", nameof(password));
        var container = await ReadContainerAsync(inputPath, ct);
        var manifest = container.Manifest;
        ValidateManifest(manifest);

        var publicKey = container.PublicKey;
        var fingerprint = SteiCrypto.Fingerprint(publicKey);
        if (!string.Equals(fingerprint, manifest.SigningKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The STEI signing key fingerprint does not match the manifest.");

        var payload = SteiCrypto.Decrypt(container.Cipher, password, Convert.FromBase64String(manifest.Salt), Convert.FromBase64String(manifest.Nonce), container.Tag);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(manifest.PayloadHash)))
            throw new CryptographicException("The STEI payload hash does not match the manifest.");
        var signatureData = BuildSignatureData(manifest, payload);
        if (!SteiCrypto.Verify(publicKey, signatureData, Convert.FromBase64String(manifest.Signature)))
            throw new CryptographicException("The STEI digital signature is invalid.");

        var tables = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, JsonElement>>>>(payload)
                     ?? throw new InvalidDataException("The STEI payload is empty or invalid.");
        var actualRowCount = tables.Values.Sum(rows => (long)rows.Count);
        if (manifest.TableCount != tables.Count || manifest.RowCount != actualRowCount)
            throw new InvalidDataException("The package manifest table/row counts do not match the encrypted payload.");
        if (expectedDatasetKey == null)
        {
            if (manifest.DataScope.StartsWith("Dataset:", StringComparison.Ordinal))
                throw new InvalidDataException("This is a dataset-specific package. Select its matching dataset type before importing.");
        }
        else if (!string.Equals(manifest.DataScope, $"Dataset:{expectedDatasetKey}", StringComparison.Ordinal) ||
                 !string.Equals(manifest.DatasetKey, expectedDatasetKey, StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(expectedTableName) ||
                 !string.Equals(manifest.DatasetTableName, expectedTableName, StringComparison.OrdinalIgnoreCase) ||
                 tables.Count != 1 ||
                 !tables.ContainsKey(expectedTableName))
        {
            throw new InvalidDataException("The encrypted package metadata, dataset key, table name and selected dataset do not match.");
        }
        else
        {
            if (replaceDatasetMode && !IsReplaceSupportedDataset(expectedDatasetKey))
                throw new InvalidOperationException("Replace mode is limited to approved master-data datasets with company-scoped soft deletion. This dataset must use update/insert mode.");

            var expectedFields = manifest.DatasetFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packagedFields = tables[expectedTableName].FirstOrDefault()?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (expectedFields.Count == 0 || !expectedFields.SetEquals(packagedFields) ||
                tables[expectedTableName].Any(row => !row.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedFields)) ||
                (manifest.DatasetFieldTypes.Count > 0 && !manifest.DatasetFieldTypes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedFields)))
                throw new InvalidDataException("The encrypted package field metadata does not match every row in its data payload.");
        }

        var provider = DatabaseRuntime.GetProvider();
        var cs = DatabaseRuntime.GetConnectionString() ?? throw new InvalidOperationException("The target database connection is not configured.");
        await using var db = CreateConnection(provider, cs);
        await db.OpenAsync(ct);

        var targetTables = new HashSet<string>(await GetTablesAsync(db, provider, ct), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var done = 0L;
        var total = 0L;
        foreach (var rows in tables.Values)
            total += rows.Count;
        var failedRows = 0L;
        var failedTables = 0;
        var deletedRows = 0L;

        if (simulation)
        {
            foreach (var pair in tables)
            {
                ct.ThrowIfCancellationRequested();
                if (!targetTables.Contains(pair.Key))
                {
                    failedTables++;
                    errors.Add($"Table not found in target database: {pair.Key}");
                    continue;
                }
                var columns = await GetColumnsAsync(db, provider, pair.Key, ct);
                if (columns.Count == 0)
                {
                    failedTables++;
                    errors.Add($"No compatible columns found: {pair.Key}");
                    continue;
                }

                var isDatasetPackage = expectedDatasetKey != null;
                var primaryKeys = isDatasetPackage
                    ? await GetPrimaryKeyColumnsAsync(db, provider, pair.Key, ct)
                    : new List<string>();
                if (isDatasetPackage && (primaryKeys.Count == 0 ||
                    primaryKeys.Any(key => !pair.Value.All(row => row.ContainsKey(key)))))
                {
                    failedTables++;
                    errors.Add($"The target table '{pair.Key}' has no discoverable primary key, or the package omits one. Safe dataset import requires all primary-key fields.");
                    continue;
                }

                if (replaceDatasetMode)
                {
                    var companyColumn = columns.FirstOrDefault(c =>
                        c.Equals("company_id", StringComparison.OrdinalIgnoreCase) ||
                        c.Equals("companyid", StringComparison.OrdinalIgnoreCase));
                    if (!columns.Contains("is_deleted", StringComparer.OrdinalIgnoreCase) ||
                        companyColumn == null || pair.Value.Any(row => !row.ContainsKey(companyColumn)))
                    {
                        failedTables++;
                        errors.Add($"Replace mode requires a soft-delete column and an explicit company field in every package row for '{pair.Key}'.");
                        continue;
                    }
                    var companyValues = pair.Value.Select(row =>
                        Convert.ToString(FromJson(row[companyColumn]), CultureInfo.InvariantCulture) ?? string.Empty)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (companyValues.Length != 1 || string.IsNullOrWhiteSpace(companyValues[0]))
                    {
                        failedTables++;
                        errors.Add($"Replace mode requires every package row in '{pair.Key}' to belong to one company.");
                        continue;
                    }
                }

                var requiredColumns = await GetRequiredColumnsAsync(db, provider, pair.Key, ct);
                var missingRequiredFields = requiredColumns
                    .Where(required => !pair.Value.All(row => row.TryGetValue(required, out var value) &&
                        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined))
                    .ToArray();
                if (missingRequiredFields.Length > 0)
                {
                    failedTables++;
                    errors.Add($"{pair.Key}: required database fields are missing or null: {string.Join(", ", missingRequiredFields)}.");
                    continue;
                }

                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in pair.Value)
                {
                    var rowError = row.Keys.FirstOrDefault(k => !columns.Contains(k, StringComparer.OrdinalIgnoreCase));
                    if (rowError != null)
                    {
                        failedRows++;
                        errors.Add($"{pair.Key}: unknown column '{rowError}'.");
                    }
                    else if (isDatasetPackage && primaryKeys.Any(key =>
                        !row.TryGetValue(key, out var value) ||
                        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                        (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))))
                    {
                        failedRows++;
                        errors.Add($"{pair.Key}: a primary-key field is empty.");
                    }
                    else if (isDatasetPackage && !seenKeys.Add(BuildSteiCompositeKey(row, primaryKeys)))
                    {
                        failedRows++;
                        errors.Add($"{pair.Key}: duplicate primary key inside the package.");
                    }
                    done++;
                    progress?.Report((done, total));
                }
            }
            var partialSim = failedRows > 0 || failedTables > 0;
            return new SteiResult(!partialSim, partialSim, total, failedRows, tables.Count, failedTables, errors, partialSim ? "Simulation found issues." : "Simulation completed successfully.");
        }

        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var importedKeysByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in tables)
            {
                ct.ThrowIfCancellationRequested();
                if (!targetTables.Contains(pair.Key))
                {
                    failedTables++;
                    errors.Add($"Table not found in target database: {pair.Key}");
                    continue;
                }
                var columns = await GetColumnsAsync(db, provider, pair.Key, ct);
                if (columns.Count == 0)
                {
                    failedTables++;
                    errors.Add($"No compatible columns found: {pair.Key}");
                    continue;
                }

                foreach (var row in pair.Value)
                {
                    try
                    {
                        var valid = row.Keys.All(k => columns.Contains(k, StringComparer.OrdinalIgnoreCase));
                        if (!valid) throw new InvalidDataException($"Column mismatch in {pair.Key}.");
                        RemapCompanyId(row, manifest.SourceCompanyId,
                            AppState.CurrentUser?.CompanyId ?? AppState.CurrentCompanyCode);
                        if (expectedDatasetKey != null)
                        {
                            var primaryKeys = await GetPrimaryKeyColumnsAsync(db, provider, pair.Key, ct);
                            if (primaryKeys.Count == 0 || primaryKeys.Any(key => !row.ContainsKey(key)))
                                throw new InvalidDataException("Safe dataset import requires all primary-key fields.");
                            if (!importedKeysByTable.TryGetValue(pair.Key, out var importedKeys))
                                importedKeysByTable[pair.Key] = importedKeys = new HashSet<string>(StringComparer.Ordinal);
                            importedKeys.Add(BuildSteiCompositeKey(row, primaryKeys));
                            if (IsProtectedAccountingDataset(expectedDatasetKey))
                                await InsertDatasetRowOnlyAsync(db, tx, provider, pair.Key, row, columns, ct);
                            else
                                await UpsertDatasetRowAsync(db, tx, provider, pair.Key, row, columns, primaryKeys, ct);
                        }
                        else
                        {
                            await InsertIgnoreAsync(db, tx, provider, pair.Key, row, columns, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedRows++;
                        errors.Add($"{pair.Key}: {ex.Message}");
                    }
                    done++;
                    progress?.Report((done, total));
                }
            }

            if (failedRows == 0 && failedTables == 0 && replaceDatasetMode && expectedDatasetKey != null)
            {
                foreach (var pair in tables)
                {
                    var columns = await GetColumnsAsync(db, provider, pair.Key, ct);
                    var companyColumn = columns.FirstOrDefault(c =>
                        c.Equals("company_id", StringComparison.OrdinalIgnoreCase) ||
                        c.Equals("companyid", StringComparison.OrdinalIgnoreCase));
                    var primaryKeys = await GetPrimaryKeyColumnsAsync(db, provider, pair.Key, ct);
                    if (companyColumn == null || !columns.Contains("is_deleted", StringComparer.OrdinalIgnoreCase) || primaryKeys.Count == 0)
                        throw new InvalidOperationException($"Replace mode is not safe for '{pair.Key}': company scope, soft-delete support, and primary keys are required.");
                    if (pair.Value.Count == 0 ||
                        !pair.Value[0].TryGetValue(companyColumn, out var companyElement))
                        throw new InvalidOperationException($"Replace mode cannot determine the company scope from package rows for '{pair.Key}'.");
                    var activeCompany = Convert.ToString(FromJson(companyElement), CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(activeCompany))
                        throw new InvalidOperationException("The package has no usable company scope for replace mode.");
                    if (pair.Value.Any(row => !row.TryGetValue(companyColumn, out var rowCompany) ||
                        !string.Equals(Convert.ToString(FromJson(rowCompany), CultureInfo.InvariantCulture), activeCompany, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"The package contains multiple company scopes for '{pair.Key}'. Replace mode was cancelled.");
                    var keep = importedKeysByTable.TryGetValue(pair.Key, out var imported)
                        ? imported : new HashSet<string>(StringComparer.Ordinal);
                    deletedRows += await SoftDeleteMissingDatasetRowsAsync(db, tx, provider, pair.Key, columns,
                        companyColumn, activeCompany, primaryKeys, keep, ct);
                }
            }

            if (failedRows > 0 || failedTables > 0)
                await tx.RollbackAsync(ct);
            else
                await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        var partial = failedRows > 0 || failedTables > 0;
        return new SteiResult(!partial, partial, total - failedRows, failedRows, tables.Count, failedTables, errors,
            partial ? "Import completed with issues; no database changes were committed." : "Import completed successfully.",
            partial ? 0 : deletedRows);
    }

    private static SteiManifest BuildManifest(string provider)
    {
        var now = DateTimeOffset.Now;
        var user = AppState.CurrentUser;
        return new SteiManifest
        {
            OperationId = Guid.NewGuid().ToString("D"),
            ExportedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            ExportedLocal = now.ToString("O"),
            ExportedByUserId = user?.Id ?? "",
            ExportedByUserName = !string.IsNullOrWhiteSpace(user?.FullName) ? user!.FullName : user?.Username ?? Environment.UserName,
            SourceCompanyId = user?.CompanyId ?? "",
            SourceCompanyCode = AppState.CurrentCompanyCode ?? AppState.CurrentUser?.CompanyId ?? "",
            SourceCompanyName = AppState.CurrentCompanyDisplayName,
            SourceDatabaseName = GetDatabaseName(provider),
            SourceDatabaseProvider = provider,
            TargetDatabaseName = GetDatabaseName(provider),
            TargetDatabaseProvider = provider,
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "",
            DataScope = "CompanyData"
        };
    }

    private static byte[] BuildSignatureData(SteiManifest manifest, byte[] payload)
    {
        var original = manifest.Signature;
        manifest.Signature = "";
        var json = SteiCrypto.Serialize(manifest);
        manifest.Signature = original;
        return SHA256.HashData(json.Concat(SHA256.HashData(payload)).ToArray());
    }

    private static async Task WriteContainerAsync(string path, SteiManifest manifest, byte[] cipher, byte[] tag, byte[] publicKey, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
        await WriteTextAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, ManifestJsonOptions), ct);
        await WriteBytesAsync(zip, "payload.bin", cipher, ct);
        await WriteBytesAsync(zip, "payload.tag", tag, ct);
        await WriteBytesAsync(zip, "signing-public.key", publicKey, ct);
        await WriteTextAsync(zip, "format.txt", Magic, ct);
    }

    private static async Task<(SteiManifest Manifest, byte[] Cipher, byte[] Tag, byte[] PublicKey)> ReadContainerAsync(string path, CancellationToken ct)
	{
		var ext = Path.GetExtension(path);
		if (!string.Equals(ext, ".stei", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ext, ".stb", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException(
            "Only .stei or .stb files are accepted by the ShouTech data exchange engine.");

    using var zip = ZipFile.OpenRead(path);
        var format = await ReadTextAsync(zip, "format.txt", ct);
        if (!string.Equals(format, Magic, StringComparison.Ordinal))
            throw new InvalidDataException("The selected file is not a valid ShouTech STEI package.");
        var manifest = JsonSerializer.Deserialize<SteiManifest>(await ReadTextAsync(zip, "manifest.json", ct)) ?? throw new InvalidDataException("Missing STEI manifest.");
        return (manifest, await ReadBytesAsync(zip, "payload.bin", ct), await ReadBytesAsync(zip, "payload.tag", ct), await ReadBytesAsync(zip, "signing-public.key", ct));
    }

    private static void ValidateManifest(SteiManifest m)
    {
        if (!string.Equals(m.Format, "SHOUTECH-STEI", StringComparison.Ordinal)) throw new InvalidDataException("Invalid STEI format.");
        if (m.FormatVersion != 1) throw new InvalidDataException($"Unsupported STEI format version: {m.FormatVersion}.");
        if (m.SchemaVersion is < 1 or > 2) throw new InvalidDataException($"Unsupported STEI schema version: {m.SchemaVersion}.");
        if (!string.Equals(m.EncryptionAlgorithm, "AES-256-GCM", StringComparison.Ordinal)) throw new CryptographicException("Unsupported STEI encryption.");
        if (!string.Equals(m.SignatureAlgorithm, "RSA-PSS-SHA256", StringComparison.Ordinal)) throw new CryptographicException("Unsupported STEI signature algorithm.");
    }

    private static DbConnection CreateConnection(string provider, string connectionString)
        => DatabaseRuntime.IsSqlite(provider) ? new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString)) : new NpgsqlConnection(connectionString);

    private static string GetDatabaseName(string provider)
    {
        try
        {
            var cs = DatabaseRuntime.GetConnectionString();
            if (DatabaseRuntime.IsSqlite(provider)) return Path.GetFileName(DatabaseRuntime.GetSqliteDataSource(cs) ?? "-");
            if (DatabaseRuntime.IsPostgreSql(provider)) return new NpgsqlConnectionStringBuilder(cs).Database ?? "-";
        }
        catch { }
        return "-";
    }

    private static async Task<List<string>> GetTablesAsync(DbConnection db, string provider, CancellationToken ct)
    {
        var sql = DatabaseRuntime.IsSqlite(provider)
            ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
            : "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name;";
        return await ReadSingleColumnAsync(db, sql, ct);
    }

    private static async Task<List<string>> GetColumnsAsync(DbConnection db, string provider, string table, CancellationToken ct)
    {
        var sql = DatabaseRuntime.IsSqlite(provider)
            ? "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\");"
            : "SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@table ORDER BY ordinal_position;";
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        if (!DatabaseRuntime.IsSqlite(provider)) AddParameter(cmd, "@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(DatabaseRuntime.IsSqlite(provider) ? 1 : 0));
        return result;
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ReadRowsAsync(DbConnection db, string provider, string table, List<string> columns, IProgress<(long done, long total)>? progress, long done, CancellationToken ct)
    {
        var qtable = Quote(table);
        var hasCompanyId = columns.Contains("company_id", StringComparer.OrdinalIgnoreCase);

        // Prefer company code from shell state; CompanyId on the user is often the same code.
        var codeValue = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(codeValue))
            codeValue = AppState.CurrentUser?.CompanyId?.Trim();
        var guidValue = AppState.CurrentUser?.CompanyId?.Trim();

        string where = "";
        if (hasCompanyId)
        {
            // CAST to text so INTEGER/TEXT company_id both match the active company code.
            if (!string.IsNullOrWhiteSpace(codeValue) && !string.IsNullOrWhiteSpace(guidValue) &&
                !string.Equals(codeValue, guidValue, StringComparison.OrdinalIgnoreCase))
            {
                where = " WHERE (CAST(company_id AS TEXT)=@companyCode OR CAST(company_id AS TEXT)=@companyGuid)";
            }
            else if (!string.IsNullOrWhiteSpace(codeValue))
            {
                where = " WHERE CAST(company_id AS TEXT)=@companyCode";
            }
            else if (!string.IsNullOrWhiteSpace(guidValue))
            {
                where = " WHERE CAST(company_id AS TEXT)=@companyGuid";
            }
        }

        var rows = await ExecuteSelectRowsAsync(db, qtable, where, guidValue, codeValue, progress, done, ct);

        // Fail closed when the active company does not match any rows. Falling
        // back to an unfiltered SELECT here could disclose other companies' data.
        return rows;
    }

    private static async Task<long> CountRowsAsync(DbConnection db, string qtable, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {qtable};";
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar is long l ? l : Convert.ToInt64(scalar ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ExecuteSelectRowsAsync(
        DbConnection db,
        string qtable,
        string where,
        string? guidValue,
        string? codeValue,
        IProgress<(long done, long total)>? progress,
        long done,
        CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {qtable}{where};";
        if (where.Contains("@companyGuid", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(guidValue))
            AddParameter(cmd, "@companyGuid", guidValue);
        if (where.Contains("@companyCode", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(codeValue))
            AddParameter(cmd, "@companyCode", codeValue);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, JsonElement>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = ToJsonElement(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
            progress?.Report((done + rows.Count, 0));
        }
        return rows;
    }


    private static void RemapCompanyId(Dictionary<string, JsonElement> row, string sourceCompanyId, string? targetCompanyId)
    {
        if (string.IsNullOrWhiteSpace(targetCompanyId) || string.IsNullOrWhiteSpace(sourceCompanyId))
            return;
        var key = row.Keys.FirstOrDefault(k => string.Equals(k, "company_id", StringComparison.OrdinalIgnoreCase));
        if (key is null) return;
        var sourceValue = Convert.ToString(FromJson(row[key]), CultureInfo.InvariantCulture);
        if (!string.Equals(sourceValue, sourceCompanyId, StringComparison.OrdinalIgnoreCase))
            return;

        if (row[key].ValueKind == JsonValueKind.Number &&
            long.TryParse(targetCompanyId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetLong))
            row[key] = JsonSerializer.SerializeToElement(targetLong);
        else if (row[key].ValueKind == JsonValueKind.Number &&
                 decimal.TryParse(targetCompanyId, NumberStyles.Number, CultureInfo.InvariantCulture, out var targetDecimal))
            row[key] = JsonSerializer.SerializeToElement(targetDecimal);
        else
            row[key] = JsonSerializer.SerializeToElement(targetCompanyId);
    }

    private static async Task<int> SoftDeleteMissingDatasetRowsAsync(
        DbConnection db, DbTransaction tx, string provider, string table, List<string> columns,
        string companyColumn, string activeCompany, IReadOnlyList<string> primaryKeys,
        HashSet<string> importedKeys, CancellationToken ct)
    {
        var deletedColumn = columns.First(c => c.Equals("is_deleted", StringComparison.OrdinalIgnoreCase));
        var deletedAtColumn = columns.FirstOrDefault(c => c.Equals("deleted_at", StringComparison.OrdinalIgnoreCase));
        await using var select = db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = $"SELECT {string.Join(",", primaryKeys.Select(Quote))} FROM {Quote(table)} " +
            $"WHERE CAST({Quote(companyColumn)} AS TEXT)=CAST(@company AS TEXT) AND ({Quote(deletedColumn)}=FALSE OR {Quote(deletedColumn)} IS NULL);";
        AddParameter(select, "@company", activeCompany);
        var missing = new List<object?[]>();
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var values = new object?[primaryKeys.Count];
                for (var i = 0; i < primaryKeys.Count; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                var key = string.Join("\u001f", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty));
                if (!importedKeys.Contains(key)) missing.Add(values);
            }
        }

        foreach (var values in missing)
        {
            await using var update = db.CreateCommand();
            update.Transaction = tx;
            var set = Quote(deletedColumn) + "=TRUE";
            if (deletedAtColumn != null) set += "," + Quote(deletedAtColumn) + "=@deletedAt";
            var predicates = primaryKeys.Select((key, i) => Quote(key) + "=@k" + i).ToList();
            predicates.Add("CAST(" + Quote(companyColumn) + " AS TEXT)=CAST(@company AS TEXT)");
            update.CommandText = $"UPDATE {Quote(table)} SET {set} WHERE {string.Join(" AND ", predicates)};";
            AddParameter(update, "@company", activeCompany);
            if (deletedAtColumn != null) AddParameter(update, "@deletedAt", DateTimeOffset.UtcNow);
            for (var i = 0; i < primaryKeys.Count; i++) AddParameter(update, "@k" + i, values[i] ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
        }
        return missing.Count;
    }

    private static async Task<List<string>> GetRequiredColumnsAsync(
        DbConnection db, string provider, string table, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            cmd.CommandText = "PRAGMA table_info(" + Quote(table) + ");";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var required = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                var notNull = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != 0;
                var hasDefault = !reader.IsDBNull(4);
                var isPrimaryKey = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) > 0;
                if (notNull && !hasDefault && !isPrimaryKey) required.Add(name);
            }
            return required;
        }

        cmd.CommandText = @"SELECT column_name
FROM information_schema.columns
WHERE table_schema='public' AND table_name=@table
  AND is_nullable='NO' AND column_default IS NULL
  AND is_identity='NO' AND is_generated='NEVER'
ORDER BY ordinal_position;";
        AddParameter(cmd, "@table", table);
        await using var pgReader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await pgReader.ReadAsync(ct)) result.Add(pgReader.GetString(0));
        return result;
    }

    private static async Task<List<string>> GetPrimaryKeyColumnsAsync(
        DbConnection db, string provider, string table, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        if (DatabaseRuntime.IsSqlite(provider))
        {
            cmd.CommandText = "PRAGMA table_info(" + Quote(table) + ");";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var keys = new List<(int Order, string Name)>();
            while (await reader.ReadAsync(ct))
            {
                var order = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
                if (order > 0) keys.Add((order, reader.GetString(1)));
            }
            return keys.OrderBy(x => x.Order).Select(x => x.Name).ToList();
        }

        cmd.CommandText = @"SELECT kcu.column_name
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu
  ON tc.constraint_name = kcu.constraint_name
 AND tc.table_schema = kcu.table_schema
 AND tc.table_name = kcu.table_name
WHERE tc.constraint_type = 'PRIMARY KEY'
  AND tc.table_schema = 'public' AND tc.table_name = @table
ORDER BY kcu.ordinal_position;";
        AddParameter(cmd, "@table", table);
        await using var pgReader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await pgReader.ReadAsync(ct)) result.Add(pgReader.GetString(0));
        return result;
    }

    private static string BuildSteiCompositeKey(
        IReadOnlyDictionary<string, JsonElement> row, IReadOnlyList<string> keys)
        => string.Join("\u001f", keys.Select(key => row.TryGetValue(key, out var value)
            ? Convert.ToString(FromJson(value), CultureInfo.InvariantCulture) ?? string.Empty
            : "<missing>"));

    // Posted documents and inventory/valuation history must never be overwritten by
    // a generic data package. Such datasets are insert-only; reversals/cancellations
    // belong to the accounting workflow and preserve the original audit trail.
    private static bool IsProtectedAccountingDataset(string key)
        => key is "Sales" or "SalesLines" or "SalesReturns" or "SalesReturnLines"
            or "Purchases" or "PurchaseLines" or "PurchaseReturns" or "PurchaseReturnLines"
            or "ReceiptVouchers" or "PaymentVouchers" or "StockMovements"
            or "StockAdjustments" or "StockAdjustmentLines" or "OpeningStock"
            or "CostLayers" or "ValuationHistory" or "ProductionOrders" or "ProductionOrderLines"
            or "PosSessions" or "StockReservations" or "Batches" or "SerialNumbers" or "ProductValuation";

    private static bool IsReplaceSupportedDataset(string key)
        => key is "Products" or "Customers" or "Suppliers" or "ProductClasses"
            or "ProductAttributes" or "ProductClassAttributes" or "ProductSuppliers"
            or "ProductBarcodes" or "ProductUnits" or "Brands" or "CustomerGroups"
            or "CustomerContacts" or "CustomerAddresses" or "SupplierGroups"
            or "SupplierContacts" or "SalesRepresentatives" or "Employees"
            or "Departments" or "JobTitles" or "Warehouses" or "WarehouseLocations"
            or "WarehouseZones" or "WarehouseTypes" or "Units";

    private static async Task UpsertDatasetRowAsync(
        DbConnection db, DbTransaction tx, string provider, string table,
        Dictionary<string, JsonElement> row, List<string> columns,
        IReadOnlyList<string> primaryKeys, CancellationToken ct)
    {
        var selected = columns.Where(c => row.ContainsKey(c)).ToList();
        if (selected.Count == 0) throw new InvalidDataException($"No compatible values were supplied for '{table}'.");
        var companyColumn = selected.FirstOrDefault(c =>
            c.Equals("company_id", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("companyid", StringComparison.OrdinalIgnoreCase));
        var predicates = primaryKeys.Select((key, i) => Quote(key) + "=@k" + i).ToList();
        var includeCompanyScope = companyColumn != null &&
            !primaryKeys.Contains(companyColumn, StringComparer.OrdinalIgnoreCase);
        if (includeCompanyScope)
            predicates.Add("CAST(" + Quote(companyColumn!) + " AS TEXT)=CAST(@companyScope AS TEXT)");
        var where = string.Join(" AND ", predicates);
        await using var update = db.CreateCommand();
        update.Transaction = tx;
        var updates = selected.Where(c => !primaryKeys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (updates.Count > 0)
        {
            update.CommandText = $"UPDATE {Quote(table)} SET " +
                string.Join(",", updates.Select((c, i) => Quote(c) + "=@u" + i)) + " WHERE " + where + ";";
            for (var i = 0; i < updates.Count; i++) AddParameter(update, "@u" + i, FromJson(row[updates[i]]));
            for (var i = 0; i < primaryKeys.Count; i++) AddParameter(update, "@k" + i, FromJson(row[primaryKeys[i]]));
            if (includeCompanyScope)
                AddParameter(update, "@companyScope", FromJson(row[companyColumn!]));
            if (await update.ExecuteNonQueryAsync(ct) > 0) return;
        }

        var inserted = await InsertDatasetRowOnlyAsync(db, tx, provider, table, row, columns, ct);
        if (inserted == 0)
            throw new InvalidDataException($"A primary-key conflict exists outside the active company scope in '{table}'. No existing record was overwritten.");
    }

    private static async Task<int> InsertDatasetRowOnlyAsync(
        DbConnection db, DbTransaction tx, string provider, string table,
        Dictionary<string, JsonElement> row, List<string> columns, CancellationToken ct)
    {
        var selected = columns.Where(c => row.ContainsKey(c)).ToList();
        if (selected.Count == 0) throw new InvalidDataException($"No compatible values were supplied for '{table}'.");
        var names = string.Join(",", selected.Select(Quote));
        var pars = string.Join(",", selected.Select((_, i) => "@p" + i));
        // Ignore duplicate keys only. INSERT OR IGNORE also suppresses NOT NULL and
        // other constraint failures in SQLite, which can silently drop valid package rows.
        var suffix = " ON CONFLICT DO NOTHING";
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {Quote(table)} ({names}) VALUES ({pars}){suffix};";
        for (var i = 0; i < selected.Count; i++) AddParameter(cmd, "@p" + i, FromJson(row[selected[i]]));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertIgnoreAsync(DbConnection db, DbTransaction tx, string provider, string table, Dictionary<string, JsonElement> row, List<string> columns, CancellationToken ct)
    {
        var selected = columns.Where(c => row.ContainsKey(c)).ToList();
        if (selected.Count == 0) return;
        var names = string.Join(",", selected.Select(Quote));
        var pars = string.Join(",", selected.Select((_, i) => "@p" + i));
        // Suppress duplicate-key conflicts only; do not hide NOT NULL or other data errors.
        var suffix = " ON CONFLICT DO NOTHING";
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {Quote(table)} ({names}) VALUES ({pars}){suffix};";
        for (var i = 0; i < selected.Count; i++) AddParameter(cmd, "@p" + i, FromJson(row[selected[i]]));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static JsonElement ToJsonElement(object? value)
    {
        if (value is byte[] bytes) return JsonSerializer.SerializeToElement(new { __binary = Convert.ToBase64String(bytes) });
        return JsonSerializer.SerializeToElement(value);
    }

    private static object? FromJson(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return DBNull.Value;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("__binary", out var b)) return Convert.FromBase64String(b.GetString() ?? "");
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when e.TryGetInt64(out var l) => l,
            JsonValueKind.Number when e.TryGetDecimal(out var d) => d,
            JsonValueKind.String => e.GetString(),
            _ => e.GetRawText()
        };
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static void AddParameter(DbCommand cmd, string name, object? value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p); }

    private static async Task<List<string>> ReadSingleColumnAsync(DbConnection db, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct) => await WriteBytesAsync(zip, name, Encoding.UTF8.GetBytes(text), ct);
    private static async Task WriteBytesAsync(ZipArchive zip, string name, byte[] data, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(data, ct);
    }
    private static async Task<string> ReadTextAsync(ZipArchive zip, string name, CancellationToken ct) => Encoding.UTF8.GetString(await ReadBytesAsync(zip, name, ct));
    private static async Task<byte[]> ReadBytesAsync(ZipArchive zip, string name, CancellationToken ct)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Missing STEI entry: {name}");
        await using var s = entry.Open();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

    // ========== BackupKeyProvider.cs ==========

    internal static class BackupKeyProvider
    {
        private static readonly object Sync = new();
        private static string? _cachedPassword;

        private static string KeyFilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ShouTech", "sec");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "bkpkey.dat");
            }
        }

        public static string GetPackagePassword()
        {
            if (_cachedPassword is not null)
                return _cachedPassword;

            lock (Sync)
            {
                if (_cachedPassword is not null)
                    return _cachedPassword;

                var path = KeyFilePath;
                byte[] rawKey;

                if (File.Exists(path))
                {
                    try
                    {
                        var protectedBlob = Convert.FromBase64String(File.ReadAllText(path).Trim());
                        rawKey = ProtectedData.Unprotect(protectedBlob, null, DataProtectionScope.CurrentUser);
                        if (rawKey.Length != 32)
                            throw new InvalidDataException("Corrupted backup key material.");
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
                    {
                        rawKey = GenerateAndStore(path);
                    }
                }
                else
                {
                    rawKey = GenerateAndStore(path);
                }

                _cachedPassword = Convert.ToBase64String(rawKey);
                CryptographicOperations.ZeroMemory(rawKey);
                return _cachedPassword;
            }
        }

        private static byte[] GenerateAndStore(string path)
        {
            var rawKey = RandomNumberGenerator.GetBytes(32);
            var protectedBlob = ProtectedData.Protect(rawKey, null, DataProtectionScope.CurrentUser);

          
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Convert.ToBase64String(protectedBlob));
            File.Move(tmp, path, overwrite: true);

            try { File.SetAttributes(path, FileAttributes.Hidden); } catch { /* effort */ }
            return rawKey;
        }
    }

    // ========== BkpServ.cs ==========

    public sealed class BkpServ : IBkpServ
    {
        private readonly ILicServ? _lic;
        private readonly IPermServ? _perm;
        private readonly ILogServ _log;
        private readonly IDevInfoSv? _devInfo;
        private readonly ISyncServ? _sync;

        public BkpServ(ILicServ lic, IPermServ perm, ILogServ log)
        {
            _lic = lic;
            _perm = perm;
            _log = log;
        }

        public BkpServ(ILogServ log, IDevInfoSv devInfo, ISyncServ sync)
        {
            _log = log;
            _devInfo = devInfo;
            _sync = sync;
        }

        public Task<string> CreateBackupAsync(string companyCode, string notes)
            => CreateBackupCoreAsync(companyCode, notes, CancellationToken.None);

        public Task<string> CreateBackupAsync(string companyCode, CancellationToken ct = default)
            => CreateBackupCoreAsync(companyCode, string.Empty, ct);

        // ═══════════════════════════════════════════════════════════
        //   shouteck_backup_dd-MM-yyyy_HH-mm_<CompanyCode>_<Note>.stb
        // ═══════════════════════════════════════════════════════════
        private async Task<string> CreateBackupCoreAsync(
            string companyCode,
            string notes,
            CancellationToken ct)
        {
            if (_lic != null)
                await _lic.EnsureCanWriteAsync(ct);

            if (_perm != null && !_perm.Has(PermKey.SysBackup))
                throw new UnauthorizedAccessException("You do not have permission to create backups.");

            if (string.IsNullOrWhiteSpace(companyCode))
                throw new ArgumentException("Company code is required.");

            var root = AppPaths.CompanyBackup(companyCode);
            Directory.CreateDirectory(root);

           
            var fileName = BuildBackupFileName(companyCode, notes);
            var fullPath = Path.Combine(root, fileName);

            var password = BackupKeyProvider.GetPackagePassword();
            var tmpPath = fullPath + ".tmp";

            try
            {
                var result = await SteiSvc.ExportAsync(tmpPath, password, progress: null, ct);
                if (!result.Success)
                {
                    _log.LogError("Backup", $"Export failed: {result.Message}");
                    throw new InvalidOperationException($"Backup export failed: {result.Message}");
                }

               
                File.Move(tmpPath, fullPath, overwrite: true);
                PruneOldBackups(root);

                _log.LogInfo("Backup",
                    $"Encrypted backup created: {fullPath} " +
                    $"({result.Tables} tables, {result.Rows} rows, AES-256-GCM, signed)");
                return fullPath;
            }
            finally
            {
               
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* أفضل جهد */ }
            }
        }

        private static string BuildBackupFileName(string companyCode, string notes)
        {
            var now = DateTime.Now;

            
            var stamp = now.ToString("dd-MM-yyyy_HH-mm", CultureInfo.InvariantCulture);

            var safeCompany = SanitizeFileNameSegment(companyCode, maxLength: 20);
            var safeNotes   = SanitizeFileNameSegment(notes,        maxLength: 40);

            if (string.IsNullOrWhiteSpace(safeCompany))
                safeCompany = "company";

            if (string.IsNullOrWhiteSpace(safeNotes))
                safeNotes = "manual";

            return $"shouteck_backup_{stamp}_{safeCompany}_{safeNotes}.stb";
        }

   
        private static string SanitizeFileNameSegment(string? input, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();

            var sb = new StringBuilder(input.Length);
            foreach (var c in input.Trim())
            {
                if (Array.IndexOf(invalid, c) >= 0) continue;
                if (c == ' ' || c == '\t') continue;     
                if (c == '.' || c == '"') continue;      
                sb.Append(c);
            }

            var cleaned = sb.ToString();
            if (cleaned.Length > maxLength)
                cleaned = cleaned.Substring(0, maxLength);

            return cleaned;
        }

        public async Task RestoreBackupAsync(string backupFilePath, CancellationToken ct = default)
        {
            if (_lic != null)
                await _lic.EnsureCanWriteAsync(ct);

            if (_perm != null && !_perm.Has(PermKey.SysBackup))
                throw new UnauthorizedAccessException("You do not have permission to restore backups.");

            if (!File.Exists(backupFilePath))
                throw new FileNotFoundException("Backup file not found.", backupFilePath);

          
            var password = BackupKeyProvider.GetPackagePassword();

            var result = await SteiSvc.ImportAsync(backupFilePath, password, simulation: false, progress: null, ct);
            if (!result.Success)
            {
                _log.LogError("Backup",
                    $"Restore failed: {result.Message} ({result.FailedTables} tables, {result.FailedRows} rows)");
                throw new InvalidOperationException($"Backup restore failed: {result.Message}");
            }

            _log.LogInfo("Backup",
                $"Backup restored: {backupFilePath} ({result.Tables} tables, {result.Rows} rows)");
        }

        public async Task<bool> ValidateBackupAsync(string backupFilePath, CancellationToken ct = default)
        {
        
            if (!File.Exists(backupFilePath))
                return false;

            try
            {
                var password = BackupKeyProvider.GetPackagePassword();
                var result = await SteiSvc.ImportAsync(backupFilePath, password, simulation: true, progress: null, ct);
                return result.Success;
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", $"Backup validation failed: {backupFilePath}", ex);
                return false;
            }
        }

        public void PerformBackup()
        {
            _ = CreateBackupAsync("001");
        }

        public BackupResult PerformRotatedBackup()
        {
            try
            {
                var task = CreateBackupAsync("001");
                var path = task.GetAwaiter().GetResult();
                return new BackupResult { Success = true, LocalPath = path };
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "PerformRotatedBackup failed", ex);
                return new BackupResult { Success = false, Message = ex.Message };
            }
        }

        public bool IsBackupDue()
        {
            try
            {
                var settings = AppCfgSvc.Current.Database.Backup;
                if (!settings.Enabled || !settings.AutoBackup)
                    return false;

                var root = AppPaths.CompanyBackup("001");
                if (!Directory.Exists(root))
                    return true;

                var latest = Directory.EnumerateFiles(root, "*.stb", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                    return true;

                var intervalHours = Math.Max(1, settings.BackupIntervalHours);
                return DateTime.UtcNow - latest.LastWriteTimeUtc >= TimeSpan.FromHours(intervalHours);
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "Unable to determine whether backup is due", ex);
                return false;
            }
        }

        private void PruneOldBackups(string directory)
        {
            try
            {
                var keep = Math.Clamp(AppCfgSvc.Current.Database.Backup.MaxBackups, 1, 1000);
                var files = Directory.EnumerateFiles(directory, "*.stb", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();

                foreach (var obsolete in files.Skip(keep))
                {
                    try { obsolete.Delete(); }
                    catch (Exception ex)
                    {
                        _log.LogError("Backup", $"Could not remove obsolete backup: {obsolete.FullName}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "Backup retention rotation failed", ex);
            }
        }

        public async Task<BackupResult> RunOnlineBackupCycleAsync(bool force = false, CancellationToken ct = default)
        {
            try
            {
                var path = await CreateBackupAsync("001", ct);
                return new BackupResult { Success = true, LocalPath = path, Uploaded = false };
            }
            catch (Exception ex)
            {
                _log.LogError("Backup", "RunOnlineBackupCycleAsync failed", ex);
                return new BackupResult { Success = false, Message = ex.Message };
            }
        }

        public Task<bool> TryUploadRotatedBackupsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        // ═══════════════════════════════════════════════════════════
        // v0.2.0 additions — required by BkpWin (v0.1.7 UI)
        // ═══════════════════════════════════════════════════════════

        public System.Collections.Generic.List<BkpFileInfo> ListBackups(string companyCode)
        {
            var result = new System.Collections.Generic.List<BkpFileInfo>();
            try
            {
                var dir = GetBackupDirectory(companyCode);
                if (!System.IO.Directory.Exists(dir))
                    return result;

                foreach (var file in System.IO.Directory.GetFiles(dir, "*.stb"))
                {
                    var fi = new System.IO.FileInfo(file);
                    result.Add(new BkpFileInfo
                    {
                        FilePath = fi.FullName,
                        FileName = fi.Name,
                        SizeBytes = fi.Length,
                        LastWriteUtc = fi.LastWriteTimeUtc,
                        IsValid = false,
                        Status = "NotValidated"
                    });
                }
            }
            catch { }
            return result;
        }

        public async System.Threading.Tasks.Task<BackupValidationDetail> ValidateBackupDetailedAsync(
            string backupFilePath,
            System.Threading.CancellationToken ct = default)
        {
            var detail = new BackupValidationDetail();
            try
            {
                if (string.IsNullOrWhiteSpace(backupFilePath) || !System.IO.File.Exists(backupFilePath))
                {
                    detail.IsValid = false;
                    detail.FailureReason = "Backup file not found.";
                    return detail;
                }

                detail.IsValid = await ValidateBackupAsync(backupFilePath, ct);
                if (!detail.IsValid)
                    detail.FailureReason = "Backup integrity validation failed.";
            }
            catch (System.Exception ex)
            {
                detail.IsValid = false;
                detail.FailureReason = ex.Message;
            }
            return detail;
        }

        public string GetBackupDirectory(string companyCode)
        {
            try
            {
                var dir = AppPaths.CompanyBackup(companyCode);

                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
            }
        }
		
		
	}

}
