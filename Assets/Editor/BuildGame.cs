using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 一键打包。菜单：Tools → 2D 平台跳跃 → 打包 …
///
/// 【为什么用脚本而不是手点 File → Build Settings】
/// 打包设置一共有二十来项，手点一次没问题，但要"再打一次"就得凭记忆复现 ——
/// 少勾一个有时代码能跑、有时直接黑屏，而且很难查。
/// 写成脚本之后，打包变成一件可重复的事，参数也顺便变成了文档。
///
/// 产物落在工程上一级的 Build/ 目录（不在 Assets 里，不会被 Unity 导入）。
/// </summary>
public static class BuildGame
{
    const string ProductName = "2Dplay";
    const string BuildRoot = "../Build";

    [MenuItem("Tools/2D 平台跳跃/打包 Windows (64 位)", false, 100)]
    public static void BuildWindows()
    {
        ApplyCommonSettings();

        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;

        Run(BuildTarget.StandaloneWindows64, BuildRoot + "/Windows", ProductName + ".exe");
    }

    [MenuItem("Tools/2D 平台跳跃/打包 WebGL (itch.io)", false, 101)]
    public static void BuildWebGL()
    {
        ApplyCommonSettings();

        PlayerSettings.SetScriptingBackend(BuildTargetGroup.WebGL, ScriptingImplementation.IL2CPP);

        // ---- WebGL 的两个关键设置 ----
        // 1. Gzip：itch.io 支持，体积比不压缩小很多
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        // 2. 解压回退：万一服务器没设 Content-Encoding 头，浏览器端的 JS 解压能兜住。
        //    不开的话在 itch.io 上大概率会卡在 loading —— 这是新手最常踩的 WebGL 坑。
        PlayerSettings.WebGL.decompressionFallback = true;

        // 这个 demo 不大，代码剥离开到最低，避免 IL2CPP 剥掉用反射调的东西
        // （PlayerController 用反射挂 PixelPerfectCamera，剥狠了会失效）
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.WebGL, ManagedStrippingLevel.Minimal);

        PlayerSettings.runInBackground = true;

        Run(BuildTarget.WebGL, BuildRoot + "/WebGL", "index.html");
    }

    [MenuItem("Tools/2D 平台跳跃/打包前自检", false, 120)]
    public static void PreBuildCheck()
    {
        int problems = 0;

        // 1. 场景保存了吗
        if (EditorSceneManagerProxy.HasUnsavedChanges())
        {
            Debug.LogError("[打包自检] 当前场景有未保存的改动 —— 先按 Ctrl+S，" +
                           "否则打包出来的是旧场景。");
            problems++;
        }

        // 2. 角色精灵的 PPU 对不对（不对的话角色会大 6 倍）
        string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { "Assets/Resources/Art" });
        int wrongPpu = 0;
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            if (!Mathf.Approximately(ti.spritePixelsPerUnit, 16f)) wrongPpu++;
        }
        if (wrongPpu > 0)
        {
            Debug.LogError(string.Format(
                "[打包自检] Resources/Art 下有 {0} 个精灵的 Pixels Per Unit 不是 16 —— " +
                "角色会明显偏大或偏小。框选那些 png，把 PPU 改成 16、Filter Mode 改成 Point。", wrongPpu));
            problems++;
        }

        // 3. 音频在不在
        if (Resources.Load<AudioClip>("Audio/sfx_jump") == null)
        {
            Debug.LogWarning("[打包自检] 找不到 Resources/Audio/sfx_jump —— 游戏会没声音。" +
                             "确认 Audio 文件夹复制到了 Assets/Resources/ 下面。");
        }

        // 4. Build Settings 里的场景列表
        if (EditorBuildSettings.scenes.Length == 0)
        {
            Debug.LogWarning("[打包自检] Build Settings 的场景列表是空的。\n" +
                             "打包脚本会自动用「当前打开的场景」，但正式打包建议手动加进去：" +
                             "File → Build Settings → Add Open Scenes。");
        }

        if (problems == 0) Debug.Log("[打包自检] 通过，可以打包。");
        else Debug.LogError(string.Format("[打包自检] 发现 {0} 个问题，先修掉再打包。", problems));
    }

    // ---------------------------------------------------------------- 内部

    static void ApplyCommonSettings()
    {
        PlayerSettings.productName = ProductName;
        PlayerSettings.companyName = "YourName";     // 发布前记得改成你自己的名字
        PlayerSettings.colorSpace = ColorSpace.Linear;   // URP 要求线性空间
        PlayerSettings.defaultIsNativeResolution = false;
    }

    /// <summary>用 Build Settings 里的场景；一个都没有就用当前打开的场景。</summary>
    static string[] CollectScenes()
    {
        var list = new List<string>();

        for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
        {
            EditorBuildSettingsScene s = EditorBuildSettings.scenes[i];
            if (s != null && s.enabled) list.Add(s.path);
        }

        if (list.Count == 0)
        {
            string current = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            if (!string.IsNullOrEmpty(current)) list.Add(current);
        }

        return list.ToArray();
    }

    static void Run(BuildTarget target, string folder, string exeName)
    {
        string[] scenes = CollectScenes();
        if (scenes.Length == 0)
        {
            Debug.LogError("[打包] 一个场景都没有。先把关卡场景保存下来，" +
                           "再到 File → Build Settings 里 Add Open Scenes。");
            return;
        }

        if (!System.IO.Directory.Exists(folder))
        {
            System.IO.Directory.CreateDirectory(folder);
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = folder + "/" + exeName,
            target = target,
            options = BuildOptions.None,
        };

        Debug.Log(string.Format("[打包] 开始：{0} → {1}（场景 {2} 个）",
                  target, options.locationPathName, scenes.Length));

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log(string.Format("[打包] 成功：{0}\n  体积 {1:0.0} MB，用时 {2:0} 秒",
                      options.locationPathName,
                      summary.totalSize / 1048576f,
                      summary.totalTime.TotalSeconds));
        }
        else if (summary.result == BuildResult.Unknown && summary.totalErrors == 0)
        {
            // 这个组合几乎只有一个原因：脚本编译没过，Unity 根本没进到打包阶段。
            // 原样打印"失败：Unknown，错误 0 个"会让人以为打包器坏了，其实不是。
            Debug.LogError(
                "[打包] 失败：脚本有编译错误，Unity 没能进入打包阶段。\n" +
                "这不是打包本身的问题 —— 打开 Console，把所有红色的 error CS 修掉再打包。\n" +
                "常见的有：CS0246（缺 using）、CS1061/CS0117（API 名字写错）、CS0120（静态方法读实例字段）。");
        }
        else
        {
            Debug.LogError(string.Format("[打包] 失败：{0}，错误 {1} 个",
                           summary.result, summary.totalErrors));
        }
    }
}

/// <summary>
/// 场景脏标记的转发。
/// 单独包一层是因为 EditorSceneManager 在 UnityEditor.SceneManagement 命名空间下，
/// 而那个命名空间在极老的版本里名字变过 —— 这样至少编译错误只会出现在这一个地方。
/// </summary>
static class EditorSceneManagerProxy
{
    public static bool HasUnsavedChanges()
    {
        UnityEngine.SceneManagement.Scene s = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        return s.isDirty;
    }
}
