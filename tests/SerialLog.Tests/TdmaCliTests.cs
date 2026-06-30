using SerialLog.Cli;

namespace SerialLog.Tests;

public class TdmaCliTests
{
    [Fact]
    public async Task Tdma_analyze_reads_logs_and_writes_result_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "serial-log-cli-" + Guid.NewGuid().ToString("N"));
        var resultFile = Path.Combine(root, "result.json");

        Directory.CreateDirectory(root);
        await File.WriteAllLinesAsync(Path.Combine(root, "center_20260630_001.log"),
        [
            "TDMA_DATA_TX,0xd2b0,12,0,0xd2b0,0x03d5,77,0,0,0",
            "TDMA_ACK_RX,0xd2b0,12,7,0xd2b0,0x03d5,77,1,0",
            "TDMA_ACK_MATCH,0xd2b0,0xd2b0,0x03d5,77,1,0"
        ]);
        await File.WriteAllLinesAsync(Path.Combine(root, "R4_20260630_001.log"),
        [
            "TDMA_DATA_LOCAL,0x03d5,12,3,0xd2b0,0x03d5,77,0,0",
            "TDMA_ACK_TX,0x03d5,12,4,0xd2b0,0x03d5,77,1,0"
        ]);

        try
        {
            var exitCode = await Program.MainAsync(
            [
                "tdma-analyze",
                "--log-dir", root,
                "--center", "0xd2b0",
                "--target", "0x03d5",
                "--out", resultFile
            ]);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"Success\": true", await File.ReadAllTextAsync(resultFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
