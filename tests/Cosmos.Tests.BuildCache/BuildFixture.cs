using System.Diagnostics;

namespace Cosmos.Tests.BuildCache;

/// <summary>
/// Shared fixture for build cache tests.
/// Resolves paths and provides build/marker helpers.
/// </summary>
public class BuildFixture
{
    private const string Arch = "x64";
    private const string Rid = "linux-x64";
    private const string Define = "ARCH_X64";

    public string RootDir { get; }
    public string ObjDir { get; }
    public string OutputDir { get; }
    public string ElfFile { get; }
    public string IsoFile { get; }
    public string PatcherHashFile { get; }
    public string IlcHashFile { get; }
    public string IlcOutput { get; }
    public string AsmObjDir { get; }
    public string CObjDir { get; }

    // Source files for change tests
    public string DevKernelCs { get; }
    public string AsmFile { get; }
    public string CFile { get; }
    public string DevKernelCDir { get; }

    private string DevKernelCsproj { get; }

    public BuildFixture()
    {
        // Walk up from test bin dir to repo root
        RootDir = FindRepoRoot();

        string objBase = Path.Combine(RootDir, "artifacts", "obj", "DevKernel", $"debug_{Rid}");
        string binBase = Path.Combine(RootDir, "artifacts", "bin", "DevKernel", $"debug_{Rid}");

        ObjDir = objBase;
        OutputDir = Path.Combine(RootDir, $"output-{Arch}");
        ElfFile = Path.Combine(binBase, "DevKernel.elf");
        IsoFile = Path.Combine(binBase, "cosmos", "DevKernel.iso");
        PatcherHashFile = Path.Combine(objBase, "cosmos", ".patcher-hash");
        IlcHashFile = Path.Combine(objBase, "cosmos", "native", ".ilc-hash");
        IlcOutput = Path.Combine(objBase, "cosmos", "native", "DevKernel.o");
        AsmObjDir = Path.Combine(objBase, "cosmos", "asm");
        CObjDir = Path.Combine(objBase, "cosmos", "cobj");

        DevKernelCsproj = Path.Combine(RootDir, "examples", "DevKernel", "DevKernel.csproj");
        DevKernelCs = Path.Combine(RootDir, "examples", "DevKernel", "Kernel.cs");
        AsmFile = Path.Combine(RootDir, "src", "Cosmos.Kernel.Native.X64", "Runtime", "Runtime.asm");
        CFile = Path.Combine(RootDir, "examples", "DevKernel", "src", "C", "test.c");
        DevKernelCDir = Path.Combine(RootDir, "examples", "DevKernel", "src", "C");
    }

    /// <summary>
    /// Run dotnet publish on the DevKernel project.
    /// </summary>
    public BuildResult Build()
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"publish -c Debug -r {Rid} " +
                        $"-p:DefineConstants=\"{Define}\" -p:CosmosArch={Arch} " +
                        $"\"{DevKernelCsproj}\" -o \"{OutputDir}\"",
            WorkingDirectory = RootDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Ensure dotnet tools are on PATH
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        if (!path.Contains(toolsDir))
        {
            psi.Environment["PATH"] = $"{toolsDir}:{path}";
        }

        using Process process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromMinutes(5));

        return new BuildResult(process.ExitCode == 0, stdout, stderr);
    }

    /// <summary>
    /// Injects a comment marker into a source file. Returns an IDisposable that reverts the change.
    /// </summary>
    public IDisposable InjectMarker(string filePath, string fileType)
    {
        string original = File.ReadAllText(filePath);
        string marker = fileType switch
        {
            "cs" => $"// CACHE_TEST_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n",
            "asm" => $"; CACHE_TEST_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n",
            "c" => $"// CACHE_TEST_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n",
            _ => $"// CACHE_TEST_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n"
        };

        File.WriteAllText(filePath, marker + original);
        return new FileRestorer(filePath, original);
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "CLAUDE.md")) &&
                Directory.Exists(Path.Combine(dir, "examples", "DevKernel")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir)!;
        }

        throw new InvalidOperationException(
            "Could not find repo root. Run tests from within the nativeaot-patcher repository.");
    }

    private sealed class FileRestorer : IDisposable
    {
        private readonly string _path;
        private readonly string _original;

        public FileRestorer(string path, string original)
        {
            _path = path;
            _original = original;
        }

        public void Dispose()
        {
            File.WriteAllText(_path, _original);
        }
    }
}

public record BuildResult(bool Success, string Stdout, string Stderr);
