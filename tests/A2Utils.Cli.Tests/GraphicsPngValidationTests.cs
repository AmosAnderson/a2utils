using System.Text.Json;

namespace A2Utils.Cli.Tests;

public sealed class GraphicsPngValidationTests
{
    [Fact]
    public void Encode_TruncatedZlibWithValidPngChunks_PreservesSourceAndExistingOutput()
    {
        string directory = Path.Combine(TestPaths.TemporaryRoot, "a2-png-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // A 40x48 PNG with all four Adler-32 footer bytes removed, and a repaired IDAT CRC.
            byte[] malformed = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACgAAAAwCAIAAADsGa/MAAAAGElEQVR42u3BMQEAAADCoPVPbQwfoAAAAAAA4G+ciStoAAAAAElFTkSuQmCC");
            string source = Path.Combine(directory, "source.png");
            string destination = Path.Combine(directory, "previous.bin");
            File.WriteAllBytes(source, malformed);
            byte[] previous = [0xa2, 0x65, 0x02];
            File.WriteAllBytes(destination, previous);
            using StringWriter output = new();
            using StringWriter error = new();
            int code = CliApplication.Run(["graphics", "encode", source, "--mode", "lores", "--to", destination,
                "--overwrite", "--json"], output, error);
            Assert.Equal(4, code);
            Assert.Equal("", output.ToString());
            using JsonDocument json = JsonDocument.Parse(error.ToString());
            Assert.Equal("png.invalid", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(malformed, File.ReadAllBytes(source));
            Assert.Equal(previous, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(directory, ".*.a2-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
