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
    }
}