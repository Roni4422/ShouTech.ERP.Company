using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests
{
    public class ExcelImportValidationTests
    {
        [Fact]
        public async Task ImportExcel_RejectsNonXlsxFiles()
        {
            var path = Path.Combine(Path.GetTempPath(), $"shoutech-{Guid.NewGuid():N}.csv");
            try
            {
                await File.WriteAllTextAsync(path, "code,name");
                var option = new DataExportOption("Products", "Products", "materials");
                var error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => DataExportSvc.ImportExcelAsync(path, option, "TEST-COMPANY"));
                Assert.Contains(".xlsx", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task ImportExcel_RejectsEmptyWorkbookInReplaceMode()
        {
            var path = Path.Combine(Path.GetTempPath(), $"shoutech-{Guid.NewGuid():N}.xlsx");
            try
            {
                XlsxWrt.Write(
                    path,
                    "Products",
                    new[] { new XlsxWrt.Col { Header = "Code" } },
                    Array.Empty<IReadOnlyList<object?>>(),
                    rtl: true,
                    hiddenHeader: "__SHOUTECH_DATASET_V1__:test-marker");

                var option = new DataExportOption("Products", "Products", "materials");
                var error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => DataExportSvc.ImportExcelAsync(path, option, "TEST-COMPANY", replaceMode: true));
                Assert.Contains("empty workbook", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task ImportExcel_RejectsWorkbookWithoutDatasetIdentity()
        {
            var path = Path.Combine(Path.GetTempPath(), $"shoutech-{Guid.NewGuid():N}.xlsx");
            try
            {
                XlsxWrt.Write(
                    path,
                    "Products",
                    new[] { new XlsxWrt.Col { Header = "Code" } },
                    new IReadOnlyList<object?>[] { new object?[] { "MAT-001" } },
                    rtl: true);

                var option = new DataExportOption("Products", "Products", "materials");
                var error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => DataExportSvc.ImportExcelAsync(path, option, "TEST-COMPANY"));
                Assert.Contains("dataset identity", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}