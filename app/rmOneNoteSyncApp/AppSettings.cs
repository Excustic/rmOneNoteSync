namespace rmOneNoteSyncApp;

public static class AppSettings
{
    // SET THIS TO TRUE FOR TESTING WITHOUT DEVICE/ONENOTE
    public static bool TestingMode { get; set; } = false;
    
    // Test mode settings
    public static class TestMode
    {
        public static bool SkipDeviceConnection { get; set; } = true;
        public static bool SkipOneNoteAuth { get; set; } = true;
        public static bool UseTestData { get; set; } = true;
        public static string TestDeviceIp { get; set; } = "10.11.99.1";
    }
}