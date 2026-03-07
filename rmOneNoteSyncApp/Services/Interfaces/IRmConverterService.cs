using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface IRmConverterService
{
    /// <summary>
    /// Converts a .rm file to InkML/HTML using the rmc tool.
    /// </summary>
    /// <param name="rmFilePath">Path to the .rm file</param>
    /// <returns>A result containing the paths to the generated InkML (XML) and HTML files.</returns>
    Task<ConversionResult> ConvertToInkMLAsync(string rmFilePath);
}

public class ConversionResult
{
    public bool Success { get; set; }
    public string? InkMLPath { get; set; }
    public string? HtmlPath { get; set; }
    public string? ErrorMessage { get; set; }
}
