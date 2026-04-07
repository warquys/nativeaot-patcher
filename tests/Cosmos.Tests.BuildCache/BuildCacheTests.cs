using System.Diagnostics;

namespace Cosmos.Tests.BuildCache;

/// <summary>
/// Integration tests for the build cache system.
/// Validates that each pipeline step (patcher, ILC, GCC, YASM, linker, ISO)
/// is correctly skipped when inputs are unchanged, and correctly rebuilt
/// when the corresponding source files change.
///
/// These tests are ordered and share state (build artifacts) across the collection.
/// </summary>
[Collection("BuildCache")]
[TestCaseOrderer("Cosmos.Tests.BuildCache.PriorityOrderer", "Cosmos.Tests.BuildCache")]
public class BuildCacheTests : IClassFixture<BuildFixture>
{
    private readonly BuildFixture _fixture;

    public BuildCacheTests(BuildFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // TEST 1: Clean build succeeds and produces all expected outputs
    // ------------------------------------------------------------------
    [Fact, TestPriority(1)]
    public void CleanBuild_ProducesAllOutputs()
    {
        // Clean intermediate state
        if (Directory.Exists(_fixture.ObjDir))
        {
            Directory.Delete(_fixture.ObjDir, true);
        }

        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Clean build failed:\n{result.Stderr}");
        Assert.True(File.Exists(_fixture.ElfFile), "ELF binary not produced");
        Assert.True(File.Exists(_fixture.IsoFile), "ISO not produced");
        Assert.True(File.Exists(_fixture.PatcherHashFile), "Patcher cache hash not written");
        Assert.True(File.Exists(_fixture.IlcHashFile), "ILC cache hash not written");
        Assert.True(File.Exists(_fixture.IlcOutput), "ILC .o not produced");

        // Clean build must NOT show any cache hits
        Assert.DoesNotContain("cache hit", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // TEST 2: No-change rebuild — all heavy steps cached
    // ------------------------------------------------------------------
    [Fact, TestPriority(2)]
    public void NoChangeRebuild_AllStepsCached()
    {
        // Capture state before rebuild
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        DateTime isoBefore = File.GetLastWriteTimeUtc(_fixture.IsoFile);
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);
        string patcherHashBefore = File.ReadAllText(_fixture.PatcherHashFile).Trim();
        string ilcHashBefore = File.ReadAllText(_fixture.IlcHashFile).Trim();

        Thread.Sleep(1100); // Ensure filesystem timestamp resolution
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"No-change rebuild failed:\n{result.Stderr}");

        // Cache hits
        Assert.Contains("Patcher cache hit", result.Stdout);
        Assert.Contains("ILC cache hit", result.Stdout);

        // Patcher should NOT run
        Assert.DoesNotContain("Batch patching:", result.Stdout);

        // ILC should NOT compile
        Assert.DoesNotContain("[ILC] Compiling:", result.Stdout);

        // Linker + ISO should NOT run (MSBuild Inputs/Outputs skip)
        Assert.DoesNotContain("Built ELF:", result.Stdout);
        Assert.DoesNotContain("ISO created at:", result.Stdout);

        // Timestamps unchanged
        Assert.Equal(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
        Assert.Equal(isoBefore, File.GetLastWriteTimeUtc(_fixture.IsoFile));
        Assert.Equal(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));

        // Hashes unchanged
        Assert.Equal(patcherHashBefore, File.ReadAllText(_fixture.PatcherHashFile).Trim());
        Assert.Equal(ilcHashBefore, File.ReadAllText(_fixture.IlcHashFile).Trim());
    }

    // ------------------------------------------------------------------
    // TEST 3: C# kernel source change → patcher + ILC + linker + ISO rebuild
    // ------------------------------------------------------------------
    [Fact, TestPriority(3)]
    public void CSharpChange_TriggersFullManagedRebuild()
    {
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);
        string patcherHashBefore = File.ReadAllText(_fixture.PatcherHashFile).Trim();
        string ilcHashBefore = File.ReadAllText(_fixture.IlcHashFile).Trim();

        Thread.Sleep(1100);
        using IDisposable marker = _fixture.InjectMarker(_fixture.DevKernelCs, "cs");
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Build after C# change failed:\n{result.Stderr}");

        // Patcher must re-run (input DLL changed)
        Assert.DoesNotContain("Patcher cache hit", result.Stdout);
        Assert.Contains("Batch patching:", result.Stdout);

        // ILC must recompile
        Assert.DoesNotContain("ILC cache hit", result.Stdout);
        Assert.Contains("[ILC] Compiling:", result.Stdout);

        // ELF + ILC output must be rebuilt
        Assert.NotEqual(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
        Assert.NotEqual(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));

        // Hashes must change
        Assert.NotEqual(patcherHashBefore, File.ReadAllText(_fixture.PatcherHashFile).Trim());
        Assert.NotEqual(ilcHashBefore, File.ReadAllText(_fixture.IlcHashFile).Trim());
    }

    // ------------------------------------------------------------------
    // TEST 4: Cache restored after C# change reverted
    // ------------------------------------------------------------------
    [Fact, TestPriority(4)]
    public void CacheRestoredAfterCSharpRevert()
    {
        // The marker was reverted by the using in test 3.
        // Rebuild should re-cache and then be stable.
        BuildResult result = _fixture.Build();
        Assert.True(result.Success, $"Rebuild failed:\n{result.Stderr}");

        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        Thread.Sleep(1100);

        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"Second rebuild failed:\n{result2.Stderr}");

        Assert.Contains("Patcher cache hit", result2.Stdout);
        Assert.Contains("ILC cache hit", result2.Stdout);
        Assert.Equal(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
    }

    // ------------------------------------------------------------------
    // TEST 5: ASM source change → YASM rebuild, patcher/ILC cached
    // ------------------------------------------------------------------
    [Fact, TestPriority(5)]
    public void AsmChange_TriggersYasmRebuild_PatcherIlcCached()
    {
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);
        string patcherHashBefore = File.ReadAllText(_fixture.PatcherHashFile).Trim();
        string ilcHashBefore = File.ReadAllText(_fixture.IlcHashFile).Trim();

        Thread.Sleep(1100);
        using IDisposable marker = _fixture.InjectMarker(_fixture.AsmFile, "asm");
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Build after ASM change failed:\n{result.Stderr}");

        // Patcher + ILC should be cached (ASM doesn't affect managed code)
        Assert.Contains("Patcher cache hit", result.Stdout);
        Assert.Contains("ILC cache hit", result.Stdout);

        // ILC output unchanged
        Assert.Equal(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));

        // ELF must be rebuilt (new .obj linked)
        Assert.NotEqual(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));

        // Managed hashes unchanged
        Assert.Equal(patcherHashBefore, File.ReadAllText(_fixture.PatcherHashFile).Trim());
        Assert.Equal(ilcHashBefore, File.ReadAllText(_fixture.IlcHashFile).Trim());
    }

    // ------------------------------------------------------------------
    // TEST 6: C source change → GCC rebuild, patcher/ILC cached
    // ------------------------------------------------------------------
    [Fact, TestPriority(6)]
    public void CChange_TriggersGccRebuild_PatcherIlcCached()
    {
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);
        string patcherHashBefore = File.ReadAllText(_fixture.PatcherHashFile).Trim();
        string ilcHashBefore = File.ReadAllText(_fixture.IlcHashFile).Trim();

        Thread.Sleep(1100);
        using IDisposable marker = _fixture.InjectMarker(_fixture.CFile, "c");
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Build after C change failed:\n{result.Stderr}");

        // Patcher + ILC cached
        Assert.Contains("Patcher cache hit", result.Stdout);
        Assert.Contains("ILC cache hit", result.Stdout);

        // ILC output unchanged
        Assert.Equal(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));

        // ELF rebuilt
        Assert.NotEqual(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));

        // Managed hashes unchanged
        Assert.Equal(patcherHashBefore, File.ReadAllText(_fixture.PatcherHashFile).Trim());
        Assert.Equal(ilcHashBefore, File.ReadAllText(_fixture.IlcHashFile).Trim());
    }

    // ------------------------------------------------------------------
    // TEST 7: GCC orphan cleanup — deleted C file's object is removed
    // ------------------------------------------------------------------
    [Fact, TestPriority(7)]
    public void GccOrphanCleanup_RemovesStaleObject()
    {
        string tempC = Path.Combine(_fixture.DevKernelCDir, "cache_test_orphan.c");

        // Create a temp C file and build
        File.WriteAllText(tempC, "// temp\nvoid cache_test_orphan_fn(void) {}\n");
        try
        {
            BuildResult result = _fixture.Build();
            Assert.True(result.Success, $"Build with temp C file failed:\n{result.Stderr}");

            // Verify object was created
            string[] orphanObjs = Directory.GetFiles(_fixture.CObjDir, "cache_test_orphan-*");
            Assert.NotEmpty(orphanObjs);
        }
        finally
        {
            // Delete the temp file
            File.Delete(tempC);
        }

        // Rebuild — orphan should be cleaned
        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"Rebuild after C deletion failed:\n{result2.Stderr}");

        string[] remaining = Directory.GetFiles(_fixture.CObjDir, "cache_test_orphan-*");
        Assert.Empty(remaining);
    }

    // ------------------------------------------------------------------
    // TEST 8: YASM content-hash changes on source edit
    // ------------------------------------------------------------------
    [Fact, TestPriority(8)]
    public void YasmContentHash_ChangesOnSourceEdit()
    {
        string asmBaseName = Path.GetFileNameWithoutExtension(_fixture.AsmFile);
        string[] originalObjs = Directory.GetFiles(_fixture.AsmObjDir, $"{asmBaseName}-*.obj");
        Assert.NotEmpty(originalObjs);
        string originalObjName = Path.GetFileName(originalObjs[0]);

        // Modify ASM and rebuild
        using (IDisposable marker = _fixture.InjectMarker(_fixture.AsmFile, "asm"))
        {
            BuildResult result = _fixture.Build();
            Assert.True(result.Success, $"Build after ASM edit failed:\n{result.Stderr}");

            string[] modifiedObjs = Directory.GetFiles(_fixture.AsmObjDir, $"{asmBaseName}-*.obj");
            Assert.NotEmpty(modifiedObjs);
            string modifiedObjName = Path.GetFileName(modifiedObjs[0]);

            // Hash-based filename must differ
            Assert.NotEqual(originalObjName, modifiedObjName);

            // Old object should be cleaned (orphan)
            Assert.False(File.Exists(originalObjs[0]), "Original ASM object should be cleaned as orphan");
        }

        // After revert, rebuild should restore original hash
        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"Build after ASM revert failed:\n{result2.Stderr}");

        string[] revertedObjs = Directory.GetFiles(_fixture.AsmObjDir, $"{asmBaseName}-*.obj");
        Assert.NotEmpty(revertedObjs);
        Assert.Equal(originalObjName, Path.GetFileName(revertedObjs[0]));
    }

    // ------------------------------------------------------------------
    // TEST 9: Full pipeline from clean intermediate state
    // ------------------------------------------------------------------
    [Fact, TestPriority(9)]
    public void CleanIntermediateRebuild_ThenCacheWorks()
    {
        // Wipe intermediate dir
        if (Directory.Exists(_fixture.ObjDir))
        {
            Directory.Delete(_fixture.ObjDir, true);
        }

        BuildResult result = _fixture.Build();
        Assert.True(result.Success, $"Clean intermediate build failed:\n{result.Stderr}");
        Assert.True(File.Exists(_fixture.IsoFile), "ISO not produced after clean rebuild");
        Assert.DoesNotContain("cache hit", result.Stdout, StringComparison.OrdinalIgnoreCase);

        // Immediate no-change rebuild
        DateTime elfTs = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        Thread.Sleep(1100);

        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"No-change rebuild failed:\n{result2.Stderr}");
        Assert.Contains("Patcher cache hit", result2.Stdout);
        Assert.Contains("ILC cache hit", result2.Stdout);
        Assert.Equal(elfTs, File.GetLastWriteTimeUtc(_fixture.ElfFile));
    }
}
