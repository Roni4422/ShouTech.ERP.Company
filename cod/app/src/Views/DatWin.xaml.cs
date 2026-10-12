using Microsoft.Win32;
using Npgsql;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Compact ShouTech data exchange window.
/// The UI deliberately does not claim a successful import/export until the data engine completes it.
/// </summary>
public partial class DatWin : Window
{
    private enum ExchangeMode { Import, Export, StructuredExport }
    private ExchangeMode _mode = ExchangeMode.Import;
    private string _lastDetails = string.Empty;
    private string _structuredExportFormat = "xlsx";
    private string? _verifiedPackagePath;
    private string? _verifiedPackageId;
    private string? _verifiedPackagePassword;
    private string? _verifiedPackageDatasetKey;
    private string? _verifiedPackageSourceCompanyCode;
    private const string LogCategory = "DataExchange";

    public DatWin() : this(ExchangeMode.Import)
    {
    }

    public DatWin(bool exportMode) : this(exportMode ? ExchangeMode.StructuredExport : ExchangeMode.Import)
    {
    }

    private DatWin(ExchangeMode mode)
    {
        _mode = mode;
        InitializeComponent();
        VisuId.ApplyWindow(this);
        Loaded += (_, _) =>
        {
            RefreshTexts();
            ApplyMode();
            RefreshMetadata();
            _ = LoadExportOptionsAsync();
        };
        AppServiceHost.Localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => AppServiceHost.Localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshTexts();
        ApplyMode();
        RefreshMetadata();
        _ = LoadExportOptionsAsync();
    }

    private static string L(string key, string fallback)
        => AppServiceHost.Localization.GetString($"DataExchange.{key}", fallback);

    private static void LogInfo(string message)
    {
        try { AppServiceHost.Logging.LogInfo(LogCategory, message); } catch { }
    }

    private static void LogWarn(string message)
    {
        try { AppServiceHost.Logging.LogWarning(LogCategory, message); } catch { }
    }

    private static void LogError(string message, Exception? ex = null)
    {
        try
        {
            if (ex != null) AppServiceHost.Logging.LogError(LogCategory, message, ex);
            else AppServiceHost.Logging.LogError(LogCategory, message);
        }
        catch { }
    }

    private static void LogOp(string action, string message)
    {
        try { AppServiceHost.Logging.LogOperation(LogCategory, action, message); } catch { }
    }


    private void RefreshTexts()
    {
        Title = L("Title", "Data Import &amp; Export");
        TitleText.Text = L("Title", "Data Import &amp; Export");
        ImportTab.Header = L("Import", "Import Data");
        ExportTab.Header = L("FullExport", "Full database package (.stei)");
        StructuredExportTab.Header = L("ExcelPdf", "Dataset export (Excel / PDF / STEI)");
        StructuredExportHintText.Text = L("ChooseDataset", "Choose a dataset and export it as Excel, PDF or an encrypted STEI package.");
        ExportFormatLabel.Text = L("Format", "Format");
        if (ExportFormatBox.Items.Count >= 3)
        {
            ((ComboBoxItem)ExportFormatBox.Items[0]).Content = L("ExcelFormat", "Excel (.xlsx)");
            ((ComboBoxItem)ExportFormatBox.Items[1]).Content = L("PdfFormat", "PDF (.pdf)");
            ((ComboBoxItem)ExportFormatBox.Items[2]).Content = L("SteiDatasetFormat", "Encrypted dataset package (.stei)");
        }
        StartButton.Content = L("Start", "Start");
        ResultTitleText.Text = L("Result", "Operation result");
        DetailsButton.Content = L("Details", "View details");
        CloseButton.Content = L("Close", "Close");
        ImportBrowseButton.ToolTip = L("Browse", "Choose file");
        ExportBrowseButton.ToolTip = L("Browse", "Choose destination");
        ImportPasswordLabel.Text = L("Password", "Package password");
        ExportPasswordLabel.Text = L("Password", "Package password");
        ExportPasswordConfirmLabel.Text = L("ConfirmPassword", "Confirm password");
        DatasetPasswordLabel.Text = L("Password", "Package password");
        DatasetPasswordConfirmLabel.Text = L("ConfirmPassword", "Confirm password");
        ImportDatasetLabel.Text = L("DatasetType", "Dataset type");
        ImportPackageInfoLabel.Text = L("PackageInfo", "Encrypted package details");
        ReadPackageInfoButton.Content = L("ReadPackageInfo", "Read file info");
        ImportHintText.Text = L("ImportDatasetHint", "Editable Excel data can be imported only with its original encrypted type marker and unchanged column headers.");
        if (ImportModeLabel != null)
            ImportModeLabel.Text = L("ImportModeLabel", "Import mode for master data (e.g. material cards)");
        if (ImportUpsertRadio != null)
            ImportUpsertRadio.Content = L("ImportUpsertMode", "Update / insert only — rows deleted from Excel stay in the database");
        if (ImportReplaceRadio != null)
            ImportReplaceRadio.Content = L("ImportReplaceMode", "Wipe missing rows to recycle bin (soft-delete) — can restore from Bin on main window");
        if (ImportReplaceHintText != null)
            ImportReplaceHintText.Text = L("ImportReplaceHint", "Example: export materials, delete some cards in Excel, then import. Upsert keeps them in DB; wipe-to-bin moves missing cards to the recycle bin so you can restore them later.");
        StructuredExportHintText.Text = L("ChooseDataset", "Select a data type to export as Excel, PDF or an encrypted STEI package. Excel files include a hidden encrypted type marker; edit data cells, not column headers.");
    }

    private static bool IsProtectedSteiDataset(string key)
        => key is "Sales" or "SalesLines" or "SalesReturns" or "SalesReturnLines"
            or "Purchases" or "PurchaseLines" or "PurchaseReturns" or "PurchaseReturnLines"
            or "ReceiptVouchers" or "PaymentVouchers" or "StockMovements"
            or "StockAdjustments" or "StockAdjustmentLines" or "OpeningStock"
            or "CostLayers" or "ValuationHistory" or "ProductionOrders" or "ProductionOrderLines"
            or "PosSessions" or "StockReservations" or "Batches" or "SerialNumbers" or "ProductValuation";

    private static bool IsReplaceSupportedSteiDataset(string key)
        => key is "Products" or "Customers" or "Suppliers" or "ProductClasses"
            or "ProductAttributes" or "ProductClassAttributes" or "ProductSuppliers"
            or "ProductBarcodes" or "ProductUnits" or "Brands" or "CustomerGroups"
            or "CustomerContacts" or "CustomerAddresses" or "SupplierGroups"
            or "SupplierContacts" or "SalesRepresentatives" or "Employees"
            or "Departments" or "JobTitles" or "Warehouses" or "WarehouseLocations"
            or "WarehouseZones" or "WarehouseTypes" or "Units";

    private void ApplyMode()
    {
        ModeTabs.SelectedItem = _mode switch
        {
            ExchangeMode.Import => ImportTab,
            ExchangeMode.Export => ExportTab,
            _ => StructuredExportTab
        };
        PathHintText.Text = _mode switch
        {
            ExchangeMode.Import => L("ReadyImport", "Choose a .stei file, then start the operation."),
            ExchangeMode.Export => L("ReadyExport", "Choose where to save the .stei file, then start the operation."),
            _ => L("ReadyStructuredExport", "Select a dataset and format, then export the selected data.")
        };
        StartButton.Content = _mode switch
        {
            ExchangeMode.Import => L("ImportNow", "Import data"),
            ExchangeMode.Export => L("ExportNow", "Export data"),
            _ => L("ExportSelected", "Export selected")
        };
        var showExportFormat = _mode != ExchangeMode.Import;
        ExportFormatLabel.Visibility = showExportFormat ? Visibility.Visible : Visibility.Collapsed;
        ExportFormatBox.Visibility = showExportFormat ? Visibility.Visible : Visibility.Collapsed;
        ExportFormatBox.IsEnabled = _mode == ExchangeMode.StructuredExport;
        if (_mode == ExchangeMode.Export)
            ExportFormatBox.SelectedIndex = 2;
        else if (_mode == ExchangeMode.StructuredExport)
            ExportFormatBox.SelectedIndex = Array.FindIndex(
                ExportFormatBox.Items.Cast<ComboBoxItem>().ToArray(),
                item => string.Equals(item.Tag?.ToString(), _structuredExportFormat, StringComparison.Ordinal));

        ResultStatusText.Text = L("Ready", "Ready");
        ResultTextBox.Text = string.Empty;
        OperationProgress.Visibility = Visibility.Collapsed;
        DetailsButton.IsEnabled = false;
        UpdateDatasetPasswordVisibility();
    }

    private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || e.Source != ModeTabs)
            return;

        _mode = ModeTabs.SelectedItem == ExportTab
            ? ExchangeMode.Export
            : ModeTabs.SelectedItem == StructuredExportTab
                ? ExchangeMode.StructuredExport
                : ExchangeMode.Import;
        ApplyMode();
    }

    private void ExportFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;

        if (_mode == ExchangeMode.StructuredExport &&
            (ExportFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { } format)
            _structuredExportFormat = format;
        UpdateDatasetPasswordVisibility();
    }

    private void UpdateDatasetPasswordVisibility()
    {
        DatasetPasswordGrid.Visibility =
            _mode == ExchangeMode.StructuredExport &&
            (ExportFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "stei"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async Task LoadExportOptionsAsync()
    {
        try
        {
            var selectedKey = (ExportItemsList.SelectedItem as DataExportOption)?.Key;
            var importSelectedKey = (ImportDatasetBox.SelectedItem as DataExportOption)?.Key;
            var options = await DataExportSvc.GetAvailableOptionsAsync();
            ExportItemsList.ItemsSource = options;
            var importOptions = new List<DataExportOption>
            {
                new("$FullPackage", L("FullDatabasePackage", "Full database package (.stei)"), string.Empty)
            };
            importOptions.AddRange(options.Where(x => !x.IsSystemInfo));
            ImportDatasetBox.ItemsSource = importOptions;
            ImportDatasetBox.SelectedItem = importOptions.FirstOrDefault(x => x.Key == importSelectedKey)
                ?? importOptions[0];
            if (options.Count > 0)
            {
                ExportItemsList.SelectedItem = options.FirstOrDefault(x => x.Key == selectedKey) ?? options[0];
                ResultStatusText.Text = L("Ready", "Ready");
            }
            else
            {
                ResultStatusText.Text = L("NoDatasets", "No exportable datasets were found.");
            }
        }
        catch (Exception ex)
        {
            ResultStatusText.Text = L("Failed", "Operation failed");
            ResultTextBox.Text = ex.Message;
            _lastDetails = ResultTextBox.Text;
            DetailsButton.IsEnabled = true;
        }
    }

    private void ImportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("Import", "Import Data"),
            Filter = L("OpenPackage", "ShouTech packages and editable Excel (*.stei;*.xlsx)|*.stei;*.xlsx"),
            CheckFileExists = true,
            Multiselect = false,
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var extension = Path.GetExtension(dialog.FileName);
        if (!string.Equals(extension, ".stei", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(L("InvalidExtension", "Choose a .stei package or a ShouTech Excel (.xlsx) export."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ImportPathBox.Text = dialog.FileName;
        ClearVerifiedPackageInfo();
        var extensionNeedsPassword = string.Equals(extension, ".stei", StringComparison.OrdinalIgnoreCase);
        ImportPasswordGrid.Visibility = extensionNeedsPassword ? Visibility.Visible : Visibility.Collapsed;
        ImportDatasetBox.IsEnabled = true;
        if (!extensionNeedsPassword)
            ImportPasswordBox.Clear();
        ResultStatusText.Text = L("Ready", "Ready");
        ResultTextBox.Text = string.Empty;
        DetailsButton.IsEnabled = false;
    }

    private void ClearVerifiedPackageInfo()
    {
        _verifiedPackagePath = null;
        _verifiedPackageId = null;
        _verifiedPackagePassword = null;
        _verifiedPackageDatasetKey = null;
        _verifiedPackageSourceCompanyCode = null;
    }

    private void ImportPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_verifiedPackagePassword != ImportPasswordBox.Password)
        {
            ClearVerifiedPackageInfo();
            if (ImportPackageInfoBox != null)
                ImportPackageInfoBox.Text = L("PackageNeedsRecheck", "Password changed. Read and verify the package details again before importing.");
        }
    }

    private async void ReadPackageInfo_Click(object sender, RoutedEventArgs e)
    {
        var path = ImportPathBox.Text.Trim();
        var password = ImportPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".stei", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(L("SelectSteiForInfo", "Choose an encrypted .stei package first."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show(L("NoPassword", "Enter the package password."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReadPackageInfoButton.IsEnabled = false;
        ImportPackageInfoBox.Text = L("ReadingPackageInfo", "Checking password, integrity, signature and package metadata...");
        try
        {
            var info = await SteiSvc.ReadDatasetPackageInfoAsync(path, password, CancellationToken.None);
            var isDataset = info.DataScope.StartsWith("Dataset:", StringComparison.Ordinal);
            if (isDataset && (string.IsNullOrWhiteSpace(info.DatasetKey) || string.IsNullOrWhiteSpace(info.DatasetTableName)))
                throw new InvalidDataException("The package is missing required dataset identity metadata.");
            var matchedOption = isDataset
                ? ImportDatasetBox.Items.Cast<object>().OfType<DataExportOption>()
                    .FirstOrDefault(option => string.Equals(option.Key, info.DatasetKey, StringComparison.Ordinal))
                : ImportDatasetBox.Items.Cast<object>().OfType<DataExportOption>()
                    .FirstOrDefault(option => option.Key == "$FullPackage");

            if (matchedOption != null)
                ImportDatasetBox.SelectedItem = matchedOption;

            var expectedName = matchedOption != null && matchedOption.Key != "$FullPackage"
                ? Path.GetFileNameWithoutExtension(DataExportSvc.GetDatasetPackageFileName(matchedOption))
                : string.Empty;
            var actualName = Path.GetFileNameWithoutExtension(path);
            var fileNameCheck = !isDataset || string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
                ? L("FileNameCheckOk", "File-name check: passed")
                : L("FileNameCheckFail", $"File-name check: expected {expectedName}.stei");
            var fieldLines = info.Fields.Count == 0
                ? L("NoFieldMetadata", "No field list is recorded in this package.")
                : string.Join(", ", info.Fields.Select(field =>
                    info.FieldTypes.TryGetValue(field, out var type) ? $"{field} ({type})" : field));
            ImportPackageInfoBox.Text = string.Join(Environment.NewLine,
                $"{L("PackageFile", "File")}: {info.FileName}",
                $"{L("PackageType", "Data type")}: {(isDataset ? info.DatasetKey : L("FullDatabasePackage", "Full database package"))}",
                $"{L("PackageTable", "Table")}: {(string.IsNullOrWhiteSpace(info.DatasetTableName) ? "-" : info.DatasetTableName)}",
                $"{L("PackageOperation", "Operation ID / type")}: {info.OperationId} / {info.OperationType}",
                $"{L("PackageExportedAt", "Exported at")}: {info.ExportedAtLocal}",
                $"{L("User", "User")}: {info.ExportedByUserName} (ID: {info.ExportedByUserId})",
                $"{L("Company", "Source company")}: {info.SourceCompanyName} | {info.SourceCompanyCode} | ID: {info.SourceCompanyId}",
                $"{L("Database", "Source database")}: {info.SourceDatabaseName} ({info.SourceDatabaseProvider})",
                $"{L("PackageVersion", "Format / schema version")}: {info.FormatVersion} / {info.SchemaVersion} | {L("Rows", "Rows")}: {info.RowCount} | {L("Tables", "Tables")}: {info.TableCount}",
                $"{L("PackageId", "Package ID")}: {info.PackageId}",
                $"{L("PackageIntegrity", "Integrity")}: {L("Verified", "Password, payload hash and embedded signature verified; signer identity is not independently certified")} | {fileNameCheck}",
                $"{L("PackageFields", "Fields and types")}: {fieldLines}");
            if (matchedOption == null)
                ImportPackageInfoBox.Text += Environment.NewLine + L("DatasetNotAvailable", "The package data type is not available in the current import list.");
            var fileNameValid = !isDataset || (matchedOption != null && string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase));
            _verifiedPackagePath = Path.GetFullPath(path);
            _verifiedPackageId = fileNameValid && matchedOption != null ? info.PackageId : null;
            _verifiedPackagePassword = password;
            _verifiedPackageDatasetKey = isDataset ? info.DatasetKey : "$FullPackage";
            _verifiedPackageSourceCompanyCode = info.SourceCompanyCode;
            LogOp("PackageMetadata.Read", $"scope={info.DataScope} | dataset={info.DatasetKey} | rows={info.RowCount} | package={info.PackageId} | sourceCompany={info.SourceCompanyCode} | user={GetCurrentUserName()}");
        }
        catch (Exception ex)
        {
            ImportPackageInfoBox.Text = L("PackageMetadataFailed", "Could not verify or read the package metadata.") + Environment.NewLine + ex.Message;
            LogWarn($"Package metadata inspection failed | file={path} | {ex.Message}");
        }
        finally
        {
            ReadPackageInfoButton.IsEnabled = true;
        }
    }

    private void ExportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var company = GetCurrentCompanyCode();
        var fileName = $"ShouTech_{SanitizeFilePart(company)}_{now:yyyyMMdd_HHmmss}.stei";

        var dialog = new SaveFileDialog
        {
            Title = L("Export", "Export Data"),
            Filter = L("SavePackage", "ShouTech Export/Import (*.stei)|*.stei"),
            DefaultExt = ".stei",
            AddExtension = true,
            FileName = fileName,
            OverwritePrompt = true,
            ValidateNames = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ExportPathBox.Text = dialog.FileName;
        ResultStatusText.Text = L("Ready", "Ready");
        ResultTextBox.Text = string.Empty;
        DetailsButton.IsEnabled = false;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        DetailsButton.IsEnabled = false;
        OperationProgress.Visibility = Visibility.Visible;
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Value = 0;
        ResultStatusText.Text = L("Running", "Operation in progress...");
        ResultTextBox.Text = string.Empty;

        try
        {
            if (_mode == ExchangeMode.StructuredExport)
            {
                if (ExportItemsList.SelectedItem is not DataExportOption option)
                {
                    MessageBox.Show(L("SelectDataset", "Select a dataset to export first."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var format = (ExportFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (format is not ("xlsx" or "pdf" or "stei"))
                {
                    MessageBox.Show(L("SelectFormat", "Select Excel, PDF or encrypted STEI as the export format."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var model = await DataExportSvc.BuildExportModelAsync(
                    option,
                    AppState.CurrentCompanyCode ?? string.Empty,
                    AppState.CurrentCompanyName ?? GetCurrentCompanyCode(),
                    GetCurrentUserName(),
                    CancellationToken.None);

                if (format == "stei")
                {
                    var password = DatasetPasswordBox.Password;
                    if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                    {
                        MessageBox.Show(L("WeakPassword", "Use a package password of at least 8 characters."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (!string.Equals(password, DatasetPasswordConfirmBox.Password, StringComparison.Ordinal))
                    {
                        MessageBox.Show(L("PasswordMismatch", "The package passwords do not match."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var dialog = new SaveFileDialog
                    {
                        Title = L("Export", "Export Data"),
                        Filter = L("SavePackage", "ShouTech dataset package (*.stei)|*.stei"),
                        DefaultExt = ".stei",
                        AddExtension = true,
                        FileName = DataExportSvc.GetDatasetPackageFileName(option),
                        OverwritePrompt = true,
                        ValidateNames = true
                    };
                    if (dialog.ShowDialog(this) != true)
                        return;

                    var progress = new Progress<(long done, long total)>(p =>
                        OperationProgress.Value = p.total <= 0 ? 0 : Math.Min(100, p.done * 100d / p.total));
                    var packageResult = await DataExportSvc.ExportDatasetPackageAsync(
                        dialog.FileName, password, option, model, progress, CancellationToken.None);
                    ShowResult(packageResult, dialog.FileName, true);
                    return;
                }

                var completed = format switch
                {
                    "pdf" => XptServ.ExportPdf(this, model),
                    _ => XptServ.ExportExcel(this, model)
                };
                ResultStatusText.Text = completed
                    ? L("Success", "Completed successfully")
                    : L("ExportNotCompleted", "Export was cancelled or could not be completed.");
                if (completed)
                {
                    var exportAt = DateTime.Now;
                    ResultTextBox.Text = string.Join(Environment.NewLine,
                        option.DisplayName,
                        $"{L("Rows", "Rows")}: {model.Rows.Count.ToString("N0", CultureInfo.CurrentCulture)}",
                        $"{L("User", "User")}: {GetCurrentUserName()}",
                        $"{L("Company", "Company")}: {GetCurrentCompanyCode()}",
                        $"{L("Date", "Date")}: {exportAt:yyyy-MM-dd}",
                        $"{L("Time", "Time")}: {exportAt:HH:mm:ss}",
                        $"{L("Format", "Format")}: {format.ToUpperInvariant()}");
                    _lastDetails = ResultTextBox.Text;
                    LogOp("Export",
                        $"{format.ToUpperInvariant()} | {option.Key} | rows={model.Rows.Count} | user={GetCurrentUserName()} | company={GetCurrentCompanyCode()} | at={exportAt:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    ResultTextBox.Text = string.Empty;
                    _lastDetails = string.Empty;
                    LogWarn($"Export cancelled or incomplete | format={format} | dataset={option.Key}");
                }
                DetailsButton.IsEnabled = false;
                return;
            }

            if (_mode == ExchangeMode.Import)
            {
                var path = ImportPathBox.Text.Trim();
                var password = ImportPasswordBox.Password;
                if (string.IsNullOrWhiteSpace(path))
                {
                    MessageBox.Show(L("NoFile", "Select an import file first."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var importExtension = Path.GetExtension(path);
                if (string.Equals(importExtension, ".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    if (ImportDatasetBox.SelectedItem is not DataExportOption importOption)
                    {
                        MessageBox.Show(L("SelectDataset", "Select the dataset type that matches the workbook."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var replaceMode = ImportReplaceRadio?.IsChecked == true;
                    if (replaceMode)
                    {
                        var confirm = MessageBox.Show(
                            L("ReplaceConfirm",
                                "This will update/insert every row in the file and move existing company rows that are not in the workbook to the recycle bin (soft-delete).\n\nYou can restore them from the Archive Bin on the main window. Continue?"),
                            Title,
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (confirm != MessageBoxResult.Yes)
                            return;
                    }

                    var actor = GetCurrentUserName();
                    var company = GetCurrentCompanyCode();
                    var startedAt = DateTime.Now;
                    LogOp("ImportExcel.Start",
                        $"key={importOption.Key} | mode={(replaceMode ? "Replace" : "Upsert")} | user={actor} | company={company} | at={startedAt:yyyy-MM-dd HH:mm:ss} | file={path}");

                    var importResult = await DataExportSvc.ImportExcelAsync(
                        path,
                        importOption,
                        AppState.CurrentCompanyCode ?? string.Empty,
                        replaceMode: replaceMode,
                        ct: CancellationToken.None);

                    var finishedAt = DateTime.Now;
                    ResultStatusText.Text = L("Success", "Completed successfully");
                    ResultTextBox.Text = string.Join(Environment.NewLine,
                        importOption.DisplayName,
                        $"{L("Mode", "Mode")}: {(replaceMode ? L("Replace", "Wipe to bin (missing rows soft-deleted)") : L("Upsert", "Update / insert only (missing rows kept)"))}",
                        $"{L("UpsertedRows", "Written rows")}: {importResult.UpsertedRows.ToString("N0", CultureInfo.CurrentCulture)}",
                        $"{L("DeletedRows", "Removed rows")}: {importResult.DeletedRows.ToString("N0", CultureInfo.CurrentCulture)}",
                        $"{L("User", "User")}: {actor}",
                        $"{L("Company", "Company")}: {company}",
                        $"{L("Date", "Date")}: {finishedAt:yyyy-MM-dd}",
                        $"{L("Time", "Time")}: {finishedAt:HH:mm:ss}",
                        replaceMode
                            ? L("ReplaceExplain", "Cards present in Excel were updated; cards removed from Excel were moved to the recycle bin for this company (restorable).")
                            : L("UpsertExplain", "Only cards present in Excel were updated or inserted. Cards you deleted from Excel remain in the database."));
                    _lastDetails = ResultTextBox.Text;
                    DetailsButton.IsEnabled = false;
                    LogOp("ImportExcel",
                        $"{importOption.Key} | mode={(replaceMode ? "Replace" : "Upsert")} | upserted={importResult.UpsertedRows} | deleted={importResult.DeletedRows} | user={actor} | company={company} | at={finishedAt:yyyy-MM-dd HH:mm:ss} | file={path}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(password))
                {
                    MessageBox.Show(L("NoPassword", "Enter the package password."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!string.Equals(Path.GetExtension(path), ".stei", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(L("InvalidExtension", "Choose a .stei package or a ShouTech Excel (.xlsx) export."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var fullPath = Path.GetFullPath(path);
                if (!string.Equals(_verifiedPackagePath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_verifiedPackagePassword, password, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(_verifiedPackageId))
                {
                    MessageBox.Show(L("VerifyPackageFirst", "Read and verify the encrypted package details before importing."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var progress = new Progress<(long done, long total)>(p => OperationProgress.Value = p.total <= 0 ? 0 : Math.Min(100, p.done * 100d / p.total));
                SteiResult result;
                if (ImportDatasetBox.SelectedItem is not DataExportOption steiOption)
                {
                    MessageBox.Show(L("SelectDataset", "Select whether this is a full package or a specific dataset package."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!string.Equals(_verifiedPackageDatasetKey, steiOption.Key, StringComparison.Ordinal))
                {
                    MessageBox.Show(L("PackageDatasetMismatch", "The selected import type does not match the verified package metadata. Read the file details again."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(_verifiedPackageSourceCompanyCode) &&
                    !string.Equals(_verifiedPackageSourceCompanyCode, GetCurrentCompanyCode(), StringComparison.OrdinalIgnoreCase))
                {
                    var confirmCompany = MessageBox.Show(
                        L("DifferentSourceCompany", $"This package was exported from company '{_verifiedPackageSourceCompanyCode}', while the active company is '{GetCurrentCompanyCode()}'. Continue only if this cross-company import is intentional."),
                        Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirmCompany != MessageBoxResult.Yes) return;
                }
                var replaceMode = ImportReplaceRadio?.IsChecked == true;
                if (replaceMode && steiOption.Key == "$FullPackage")
                {
                    MessageBox.Show(L("FullPackageReplaceUnsupported", "Replace mode applies only to one verified dataset package, not to full-database packages. Select update/insert mode instead."),
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (replaceMode && !IsReplaceSupportedSteiDataset(steiOption.Key))
                {
                    MessageBox.Show(L("DatasetReplaceBlocked", "Replace mode is limited to approved master-data datasets that support company-scoped soft deletion. Use update/insert mode for this dataset; accounting and inventory history must be preserved."),
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var preflight = steiOption.Key == "$FullPackage"
                    ? await SteiSvc.ImportAsync(path, password, simulation: true, progress: progress, ct: CancellationToken.None)
                    : await DataExportSvc.ImportDatasetPackageAsync(path, password, steiOption, simulation: true, progress: progress, ct: CancellationToken.None, replaceMode: replaceMode);
                if (!preflight.Success)
                {
                    ShowResult(preflight, path, false);
                    ResultStatusText.Text = L("PreflightFailed", "Pre-import validation failed");
                    return;
                }

                ResultTextBox.Text = string.Join(Environment.NewLine,
                    L("PreflightPassed", "Pre-import structural validation passed."),
                    $"{L("Rows", "Rows")}: {preflight.Rows.ToString("N0", CultureInfo.CurrentCulture)}",
                    $"{L("Tables", "Tables")}: {preflight.Tables}",
                    L("PreflightNote", "The final import still checks database constraints; no data has been written yet."));
                _lastDetails = ResultTextBox.Text;

                if (steiOption.Key == "$FullPackage")
                {
                    var confirmFull = MessageBox.Show(L("FullPackageConfirm", "Structural validation passed. This is a full database package and may affect many tables. Continue only if you trust the source and have a current backup."), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirmFull != MessageBoxResult.Yes) return;
                    result = await SteiSvc.ImportAsync(path, password, simulation: false, progress: progress, ct: CancellationToken.None);
                }
                else
                {
                    var actor = GetCurrentUserName();
                    var company = GetCurrentCompanyCode();
                    var modeName = replaceMode ? "ReplaceToBin" : (IsProtectedSteiDataset(steiOption.Key) ? "InsertOnly" : "Upsert");
                    var confirmText = replaceMode
                        ? $"Validation passed for '{steiOption.DisplayName}'. Update/insert {preflight.Rows} rows and move missing active rows for the current company to the recycle bin (soft-delete)?"
                        : $"Validation passed for '{steiOption.DisplayName}'. Import {preflight.Rows} rows into the active company? Reference data will be updated/inserted; posted accounting and inventory-history datasets are insert-only.";
                    var confirmDataset = MessageBox.Show(
                        L("DatasetImportConfirm", confirmText),
                        Title, MessageBoxButton.YesNo, replaceMode ? MessageBoxImage.Warning : MessageBoxImage.Question);
                    if (confirmDataset != MessageBoxResult.Yes) return;
                    var startedAt = DateTime.Now;
                    LogOp("ImportSTEI.Dataset.Start",
                        $"key={steiOption.Key} | mode={modeName} | user={actor} | company={company} | at={startedAt:yyyy-MM-dd HH:mm:ss} | file={path}");
                    result = await DataExportSvc.ImportDatasetPackageAsync(path, password, steiOption, simulation: false, progress: progress, ct: CancellationToken.None, replaceMode: replaceMode);
                    LogOp("ImportSTEI.Dataset",
                        $"key={steiOption.Key} | mode={modeName} | success={result.Success} | rows={result.Rows} | failedRows={result.FailedRows} | failedTables={result.FailedTables} | deletedRows={result.DeletedRows} | user={actor} | company={company} | at={DateTime.Now:yyyy-MM-dd HH:mm:ss} | file={path}");
                }
                ShowResult(result, path, false);
            }
            else
            {
                var path = ExportPathBox.Text.Trim();
                var password = ExportPasswordBox.Password;
                var confirm = ExportPasswordConfirmBox.Password;
                if (string.IsNullOrWhiteSpace(path))
                {
                    MessageBox.Show(L("NoDestination", "Select an export destination first."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                {
                    MessageBox.Show(L("WeakPassword", "Use a package password of at least 8 characters."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!string.Equals(password, confirm, StringComparison.Ordinal))
                {
                    MessageBox.Show(L("PasswordMismatch", "The package passwords do not match."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var progress = new Progress<(long done, long total)>(p => OperationProgress.Value = p.total <= 0 ? 0 : Math.Min(100, p.done * 100d / p.total));
                var result = await SteiSvc.ExportAsync(path, password, progress: progress, ct: CancellationToken.None);
                ShowResult(result, path, true);
            }
        }
        catch (OperationCanceledException)
        {
            ResultStatusText.Text = L("Cancelled", "Operation cancelled");
            LogWarn($"Data exchange cancelled | mode={_mode}");
        }
        catch (Exception ex)
        {
            ResultStatusText.Text = L("Failed", "Operation failed");
            ResultTextBox.Text = BuildFailureText(ex);
            _lastDetails = ResultTextBox.Text;
            DetailsButton.IsEnabled = true;
            LogError($"Data exchange failed | mode={_mode} | {ex.Message}", ex);
        }
        finally
        {
            OperationProgress.Visibility = Visibility.Collapsed;
            ImportPasswordBox.Clear();
            ExportPasswordBox.Clear();
            ExportPasswordConfirmBox.Clear();
            DatasetPasswordBox.Clear();
            DatasetPasswordConfirmBox.Clear();
            StartButton.IsEnabled = true;
        }
    }

    private void ShowResult(SteiResult result, string path, bool export)
    {
        ResultStatusText.Text = result.Success
            ? L("Success", "Completed successfully")
            : result.Partial
                ? L("Partial", "Completed with issues")
                : L("Failed", "Operation failed");

        var lines = new List<string>
        {
            export ? L("ExportResult", "Export result") : L("ImportResult", "Import result"),
            new string('─', 42),
            $"{L("Message", "Message")}: {result.Message}",
            $"{L("Rows", "Rows")}: {result.Rows.ToString("N0", CultureInfo.InvariantCulture)}",
            $"{L("FailedRows", "Failed rows")}: {result.FailedRows.ToString("N0", CultureInfo.InvariantCulture)}",
            $"{L("Tables", "Tables")}: {result.Tables.ToString(CultureInfo.InvariantCulture)}",
            $"{L("FailedTables", "Failed tables")}: {result.FailedTables.ToString(CultureInfo.InvariantCulture)}",
            $"{L("Date", "Date")}: {DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"{L("Time", "Time")}: {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}",
            $"{L("User", "User")}: {GetCurrentUserName()}",
            $"{L("Company", "Company")}: {GetCurrentCompanyCode()}",
            $"{L("Database", "Database")}: {GetCurrentDatabaseName()}",
            $"{L("Provider", "Provider")}: {GetCurrentProvider()}",
            $"{(export ? L("OutputFile", "Output file") : L("InputFile", "Input file"))}: {path}"
        };
        if (result.DeletedRows > 0)
            lines.Add($"{L("DeletedRows", "Moved to recycle bin (soft-deleted)")}: {result.DeletedRows.ToString("N0", CultureInfo.InvariantCulture)}");

        if (result.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(L("Errors", "Details"));
            lines.AddRange(result.Errors.Take(20).Select((x, i) => $"{i + 1}. {x}"));
            if (result.Errors.Count > 20) lines.Add($"... +{result.Errors.Count - 20} {L("More", "more")}");
        }
        ResultTextBox.Text = string.Join(Environment.NewLine, lines);
        _lastDetails = ResultTextBox.Text;
        DetailsButton.IsEnabled = result.Errors.Count > 0;
    }

    private static string BuildFailureText(Exception ex)
        => string.Join(Environment.NewLine,
            L("Failed", "Operation failed"),
            new string('─', 42),
            $"{L("Date", "Date")}: {DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"{L("Time", "Time")}: {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}",
            $"{L("User", "User")}: {GetCurrentUserName()}",
            $"{L("Company", "Company")}: {GetCurrentCompanyCode()}",
            $"{L("Database", "Database")}: {GetCurrentDatabaseName()}",
            $"{L("Provider", "Provider")}: {GetCurrentProvider()}",
            $"{L("Error", "Error")}: {ex.Message}");

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastDetails))
            return;

        MessageBox.Show(_lastDetails, L("Details", "Details"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshMetadata()
    {
        MetadataText.Text = string.Join("  •  ",
            $"{L("User", "User")}: {GetCurrentUserName()}",
            $"{L("Company", "Company")}: {GetCurrentCompanyCode()}",
            $"{L("Database", "Database")}: {GetCurrentDatabaseName()}",
            $"{L("Provider", "Provider")}: {GetCurrentProvider()}");
    }

    private static string GetCurrentUserName()
    {
        try
        {
            var user = AppState.CurrentUser;
            if (user is not null)
                return !string.IsNullOrWhiteSpace(user.FullName) ? user.FullName : user.Username;
        }
        catch { }
        return Environment.UserName;
    }

    private static string GetCurrentCompanyCode()
        => string.IsNullOrWhiteSpace(AppState.CurrentCompanyCode) ? "-" : AppState.CurrentCompanyCode!;

    private static string GetCurrentDatabaseName()
    {
        try
        {
            var connection = DatabaseRuntime.GetConnectionString();
            if (DatabaseRuntime.IsSqlite())
            {
                var source = DatabaseRuntime.GetSqliteDataSource(connection);
                if (!string.IsNullOrWhiteSpace(source) && !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(source);
            }
            else if (DatabaseRuntime.IsPostgreSql() && !string.IsNullOrWhiteSpace(connection))
            {
                var builder = new NpgsqlConnectionStringBuilder(connection);
                if (!string.IsNullOrWhiteSpace(builder.Database))
                    return builder.Database;
            }
        }
        catch { }

        return "-";
    }

    private static string GetCurrentProvider()
        => DatabaseRuntime.GetProvider();

    private static string SanitizeFilePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Company";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);

        return builder.ToString();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
