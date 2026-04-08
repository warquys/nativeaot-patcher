// This code is licensed under MIT license (see LICENSE for details)
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Cosmos.Build.GCC.Tasks;

public sealed class GCCBuildTask : ToolTask
{
    [Required] public string? GCCPath { get; set; }
    [Required] public string[]? SourceFiles { get; set; }
    [Required] public string? OutputPath { get; set; }

    // Optional additional compiler flags
    public string? CompilerFlags { get; set; }

    // ILC path for linking
    public string? IlcPath { get; set; }

    protected override MessageImportance StandardErrorLoggingImportance => MessageImportance.Normal;

    protected override string GenerateFullPathToTool() => GCCPath!;

    protected override string GenerateCommandLineCommands() => string.Empty;

    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, "Running Cosmos.GCC Build Task...");
        Log.LogMessage(MessageImportance.High, $"Tool Path: {GCCPath}");
        Log.LogMessage(MessageImportance.High, $"Output Path: {OutputPath}");

        if (!Directory.Exists(OutputPath))
        {
            Log.LogMessage(MessageImportance.Low, $"[Debug] Creating output directory: {OutputPath}");
            Directory.CreateDirectory(OutputPath!);
        }

        if (SourceFiles == null || SourceFiles.Length == 0)
        {
            Log.LogMessage(MessageImportance.Normal, "No C source files to compile.");
            return true;
        }

        Log.LogMessage(MessageImportance.Normal, $"Found {SourceFiles.Length} C files to compile");

        // Get GCC's freestanding include directory
        string? gccIncludePath = GetGCCIncludePath();
        if (gccIncludePath != null)
        {
            Log.LogMessage(MessageImportance.Normal, $"Using GCC include path: {gccIncludePath}");
        }

        // Validate GCC path exists (once, before the loop)
        string toolPath = GenerateFullPathToTool().Trim();
        GCCPath = toolPath; // normalize for downstream checks

        if (!File.Exists(toolPath) && !TestGCCInPath())
        {
            Log.LogError($"GCC not found at {toolPath}. Ensure the cross-compiler is installed and on PATH.");
            return false;
        }

        // Execute the GCC command for each C file (with incremental support via content-hash filenames)
        using SHA1? hasher = SHA1.Create();
        var validOutputFiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string objExt = Path.DirectorySeparatorChar == '\\' ? ".obj" : ".o";

        foreach (string file in SourceFiles)
        {
            // Compute hash of file contents for a deterministic output filename
            using FileStream stream = File.OpenRead(file);
            byte[] fileHash = hasher.ComputeHash(stream);
            string fileHashString = BitConverter.ToString(fileHash).Replace("-", "").ToLower();

            string baseName = Path.GetFileNameWithoutExtension(file);
            string outputName = $"{baseName}-{fileHashString.Substring(0, 8)}{objExt}";
            string outputPath = Path.GetFullPath(Path.Combine(OutputPath!, outputName));

            validOutputFiles.Add(outputPath);

            // Skip if output already exists (content-hash filename = up-to-date)
            if (File.Exists(outputPath))
            {
                Log.LogMessage(MessageImportance.Normal, $"Skipping {file} (up to date: {outputName})");
                continue;
            }

            // Build and execute the command for this file
            StringBuilder sb = new();
            sb.Append(" -c ");
            sb.Append($" -o {outputPath} ");

            if (!string.IsNullOrEmpty(CompilerFlags))
            {
                sb.Append($" {CompilerFlags} ");
            }

            if (gccIncludePath != null)
            {
                sb.Append($" -I{gccIncludePath} ");
            }

            // Per-file include path: the directory containing this source file
            string fileDir = Path.GetDirectoryName(file)!;
            sb.Append($" -I{fileDir} ");

            sb.Append($" {file} ");
            string commandLineArguments = sb.ToString();
            Log.LogMessage(MessageImportance.Normal, $"Compiling {file} with args: {commandLineArguments}");

            if (!ExecuteCommand(toolPath, commandLineArguments))
            {
                Log.LogError($"Failed to compile {file}");
                Log.LogError($"Command: {GenerateFullPathToTool()} {commandLineArguments}");
                return false;
            }
        }

        // Remove orphan object files (from renamed/deleted source files)
        foreach (string existing in Directory.GetFiles(OutputPath!, "*" + objExt))
        {
            string normalizedExisting = Path.GetFullPath(existing);
            if (!validOutputFiles.Contains(normalizedExisting))
            {
                Log.LogMessage(MessageImportance.Normal, $"Removing orphan object: {Path.GetFileName(existing)}");
                File.Delete(existing);
            }
        }

        Log.LogMessage(MessageImportance.High, "GCCBuildTask completed successfully.");
        return true;
    }

    protected override string ToolName => "Cosmos.GCC";

    private string? GetGCCIncludePath()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = GCCPath,
                Arguments = "--print-file-name=include",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new System.Diagnostics.Process();
            process.StartInfo = psi;
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                return output;
            }
        }
        catch
        {
            // If we can't get the include path, continue without it
        }
        return null;
    }

    private bool TestGCCInPath()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = GCCPath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new System.Diagnostics.Process();
            process.StartInfo = psi;
            process.Start();
            process.WaitForExit();

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private bool ExecuteCommand(string toolPath, string arguments)
    {
        // Create process start info
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Start the process
        using var process = new System.Diagnostics.Process();
        process.StartInfo = psi;
        process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.LogMessage(MessageImportance.Normal, e.Data);
                    }
                };
        process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.LogError(e.Data);
                    }
                };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return process.ExitCode == 0;
    }
}
