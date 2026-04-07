namespace Cosmos.Tests.BuildCache;

/// <summary>
/// Integration tests for the build cache system.
/// Validates that each pipeline step (patcher, ILC, GCC, YASM, linker, ISO)
/// is correctly skipped when inputs are unchanged, and correctly rebuilt
/// when the corresponding source files change.
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
    // TEST 1: Clean build — full pipeline, no cache hits
    // ------------------------------------------------------------------
    [Fact, TestPriority(1)]
    public void CleanBuild_ProducesAllOutputs()
    {
        if (Directory.Exists(_fixture.ObjDir))
        {
            Directory.Delete(_fixture.ObjDir, true);
        }

        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Clean build failed:\n{result.Output}");
        Assert.True(File.Exists(_fixture.ElfFile), "ELF binary not produced");
        Assert.True(File.Exists(_fixture.IsoFile), "ISO not produced");
        Assert.True(File.Exists(_fixture.PatcherHashFile), "Patcher cache hash not written");
        Assert.True(File.Exists(_fixture.IlcHashFile), "ILC cache hash not written");
        Assert.True(File.Exists(_fixture.LinkHashFile), "Link cache hash not written");
        Assert.True(File.Exists(_fixture.IsoHashFile), "ISO cache hash not written");
        Assert.True(File.Exists(_fixture.IlcOutput), "ILC .o not produced");

        Assert.DoesNotContain("cache hit", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // TEST 2: No-change rebuild — ALL steps cached
    // ------------------------------------------------------------------
    [Fact, TestPriority(2)]
    public void NoChangeRebuild_AllStepsCached()
    {
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        DateTime isoBefore = File.GetLastWriteTimeUtc(_fixture.IsoFile);
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);

        Thread.Sleep(1100);
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"No-change rebuild failed:\n{result.Output}");

        // All cache hits
        Assert.Contains("Patcher cache hit", result.Stdout);
        Assert.Contains("ILC cache hit", result.Stdout);
        Assert.Contains("Linker cache hit", result.Stdout);
        Assert.Contains("ISO cache hit", result.Stdout);

        // Nothing should run
        Assert.DoesNotContain("Batch patching:", result.Stdout);
        Assert.DoesNotContain("[ILC] Compiling:", result.Stdout);
        Assert.DoesNotContain("Built ELF:", result.Stdout);
        Assert.DoesNotContain("ISO created at:", result.Stdout);

        // Timestamps unchanged
        Assert.Equal(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
        Assert.Equal(isoBefore, File.GetLastWriteTimeUtc(_fixture.IsoFile));
        Assert.Equal(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));
    }

    // ------------------------------------------------------------------
    // TEST 3: C# change → patcher + ILC + linker + ISO rebuild
    // ------------------------------------------------------------------
    [Fact, TestPriority(3)]
    public void CSharpChange_TriggersFullManagedRebuild()
    {
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);

        Thread.Sleep(1100);
        using IDisposable marker = _fixture.InjectMarker(_fixture.DevKernelCs, "cs");
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Build after C# change failed:\n{result.Output}");

        // Patcher + ILC must re-run
        Assert.DoesNotContain("Patcher cache hit", result.Stdout);
        Assert.Contains("Batch patching:", result.Stdout);
        Assert.DoesNotContain("ILC cache hit", result.Stdout);
        Assert.Contains("[ILC] Compiling:", result.Stdout);

        // Linker + ISO must rebuild (ILC output changed)
        Assert.DoesNotContain("Linker cache hit", result.Stdout);
        Assert.DoesNotContain("ISO cache hit", result.Stdout);

        // Timestamps must change
        Assert.NotEqual(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
        Assert.NotEqual(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));
    }

    // ------------------------------------------------------------------
    // TEST 4: Cache restored after C# change reverted
    // ------------------------------------------------------------------
    [Fact, TestPriority(4)]
    public void CacheRestoredAfterCSharpRevert()
    {
        // Marker was reverted by using in test 3. Two builds to stabilize + verify.
        BuildResult result = _fixture.Build();
        Assert.True(result.Success, $"Rebuild failed:\n{result.Output}");

        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"Second rebuild failed:\n{result2.Output}");

        Assert.Contains("Patcher cache hit", result2.Stdout);
        Assert.Contains("ILC cache hit", result2.Stdout);
        Assert.Contains("Linker cache hit", result2.Stdout);
        Assert.Contains("ISO cache hit", result2.Stdout);
    }

    // ------------------------------------------------------------------
    // TEST 5: ASM change → YASM + linker + ISO rebuild, patcher/ILC cached
    // ------------------------------------------------------------------
    [Fact, TestPriority(5)]
    public void AsmChange_TriggersYasmRebuild_PatcherIlcCached()
    {
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);

        Thread.Sleep(1100);
        using IDisposable marker = _fixture.InjectMarker(_fixture.AsmFile, "asm");
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Build after ASM change failed:\n{result.Output}");

        // Patcher + ILC cached (ASM doesn't affect managed code)
        Assert.Contains("Patcher cache hit", result.Stdout);
        Assert.Contains("ILC cache hit", result.Stdout);
        Assert.Equal(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));

        // Linker + ISO must rebuild (new .obj)
        Assert.DoesNotContain("Linker cache hit", result.Stdout);
        Assert.DoesNotContain("ISO cache hit", result.Stdout);
        Assert.NotEqual(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
    }

    // ------------------------------------------------------------------
    // TEST 6: C change → GCC + linker + ISO rebuild, patcher/ILC cached
    // ------------------------------------------------------------------
    [Fact, TestPriority(6)]
    public void CChange_TriggersGccRebuild_PatcherIlcCached()
    {
        DateTime ilcBefore = File.GetLastWriteTimeUtc(_fixture.IlcOutput);
        DateTime elfBefore = File.GetLastWriteTimeUtc(_fixture.ElfFile);

        Thread.Sleep(1100);
        using IDisposable marker = _fixture.InjectMarker(_fixture.CFile, "c");
        BuildResult result = _fixture.Build();

        Assert.True(result.Success, $"Build after C change failed:\n{result.Output}");

        // Patcher + ILC cached
        Assert.Contains("Patcher cache hit", result.Stdout);
        Assert.Contains("ILC cache hit", result.Stdout);
        Assert.Equal(ilcBefore, File.GetLastWriteTimeUtc(_fixture.IlcOutput));

        // Linker + ISO must rebuild (new .o)
        Assert.DoesNotContain("Linker cache hit", result.Stdout);
        Assert.DoesNotContain("ISO cache hit", result.Stdout);
        Assert.NotEqual(elfBefore, File.GetLastWriteTimeUtc(_fixture.ElfFile));
    }

    // ------------------------------------------------------------------
    // TEST 7: GCC orphan cleanup — deleted C file's object is removed
    // ------------------------------------------------------------------
    [Fact, TestPriority(7)]
    public void GccOrphanCleanup_RemovesStaleObject()
    {
        string tempC = Path.Combine(_fixture.DevKernelCDir, "cache_test_orphan.c");

        File.WriteAllText(tempC, "// temp\nvoid cache_test_orphan_fn(void) {}\n");
        try
        {
            BuildResult result = _fixture.Build();
            Assert.True(result.Success, $"Build with temp C file failed:\n{result.Output}");

            string[] orphanObjs = Directory.GetFiles(_fixture.CObjDir, "cache_test_orphan-*");
            Assert.NotEmpty(orphanObjs);
        }
        finally
        {
            File.Delete(tempC);
        }

        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"Rebuild after C deletion failed:\n{result2.Output}");

        string[] remaining = Directory.GetFiles(_fixture.CObjDir, "cache_test_orphan-*");
        Assert.Empty(remaining);
    }

    // ------------------------------------------------------------------
    // TEST 8: YASM content-hash changes on source edit and reverts cleanly
    // ------------------------------------------------------------------
    [Fact, TestPriority(8)]
    public void YasmContentHash_ChangesOnSourceEdit()
    {
        string asmBaseName = Path.GetFileNameWithoutExtension(_fixture.AsmFile);
        string[] originalObjs = Directory.GetFiles(_fixture.AsmObjDir, $"{asmBaseName}-*.obj");
        Assert.NotEmpty(originalObjs);
        string originalObjName = Path.GetFileName(originalObjs[0]);

        using (IDisposable marker = _fixture.InjectMarker(_fixture.AsmFile, "asm"))
        {
            BuildResult result = _fixture.Build();
            Assert.True(result.Success, $"Build after ASM edit failed:\n{result.Output}");

            string[] modifiedObjs = Directory.GetFiles(_fixture.AsmObjDir, $"{asmBaseName}-*.obj");
            Assert.NotEmpty(modifiedObjs);
            string modifiedObjName = Path.GetFileName(modifiedObjs[0]);

            Assert.NotEqual(originalObjName, modifiedObjName);
            Assert.False(File.Exists(originalObjs[0]), "Original ASM object should be cleaned as orphan");
        }

        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"Build after ASM revert failed:\n{result2.Output}");

        string[] revertedObjs = Directory.GetFiles(_fixture.AsmObjDir, $"{asmBaseName}-*.obj");
        Assert.NotEmpty(revertedObjs);
        Assert.Equal(originalObjName, Path.GetFileName(revertedObjs[0]));
    }

    // ------------------------------------------------------------------
    // TEST 9: Full pipeline from clean intermediate, then cache works
    // ------------------------------------------------------------------
    [Fact, TestPriority(9)]
    public void CleanIntermediateRebuild_ThenCacheWorks()
    {
        if (Directory.Exists(_fixture.ObjDir))
        {
            Directory.Delete(_fixture.ObjDir, true);
        }

        BuildResult result = _fixture.Build();
        Assert.True(result.Success, $"Clean intermediate build failed:\n{result.Output}");
        Assert.True(File.Exists(_fixture.IsoFile), "ISO not produced after clean rebuild");
        Assert.DoesNotContain("cache hit", result.Stdout, StringComparison.OrdinalIgnoreCase);

        BuildResult result2 = _fixture.Build();
        Assert.True(result2.Success, $"No-change rebuild failed:\n{result2.Output}");
        Assert.Contains("Patcher cache hit", result2.Stdout);
        Assert.Contains("ILC cache hit", result2.Stdout);
        Assert.Contains("Linker cache hit", result2.Stdout);
        Assert.Contains("ISO cache hit", result2.Stdout);
    }
}
