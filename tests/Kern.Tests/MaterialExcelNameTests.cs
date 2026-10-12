using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests
{
    public class MaterialExcelNameTests
    {
        [Fact]
        public void ExportUsesNeutralNameHeaderForArabicNameField()
        {
            Assert.Equal("name", DataExportSvc.GetExportHeaderLabel("materials", "name_ar", "Name"));
            Assert.Equal("English Name", DataExportSvc.GetExportHeaderLabel("materials", "name_en", "English Name"));
            Assert.Equal("Arabic Name", DataExportSvc.GetExportHeaderLabel("customers", "name_ar", "Arabic Name"));
        }

        [Fact]
        public void BlankMaterialNameIsNormalizedToEmptyString()
        {
            Assert.Equal(string.Empty, DataExportSvc.NormalizeImportedValue("materials", "name_ar", null));
            Assert.Equal("Tea", DataExportSvc.NormalizeImportedValue("materials", "name_ar", "Tea"));
            Assert.Null(DataExportSvc.NormalizeImportedValue("customers", "name_ar", null));
        }
    }
}