using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

/// <summary>
/// 一键生成完整教学关（W4）。
///
/// 菜单：Tools → 2D 平台跳跃 → 生成教学关
///
/// 它会做四件事：
///   1. 把 Assets/Tiles 里 14 个 Tile 的 Collider Type 修对（实心=Grid、尖刺=Sprite、装饰=None）
///   2. 删掉场景里所有旧的 Grid，重新建一个 Grid + 三层 Tilemap（Ground / Hazard / Background）
///   3. 按下面的关卡布局画满整关，并放好出生点和敌人
///   4. **保存场景**（顺手解决"改了没存"的问题）
///
/// 关卡设计依据见 关卡设计指南.md：一屏一个想法、教 → 用 → 变 → 组合 → 释放。
/// 宽度 440 格 = 11 屏，每屏 40 格。
/// </summary>
public static class TutorialLevelBuilder
{
    const string TileFolder = "Assets/Tiles/";

    // 主路地面所在的格子行。行 -1 的顶面正好是世界坐标 y = 0。
    const int MainSurface = -1;

    // 关卡总宽（格）
    const int LevelWidth = 782;

    // 三层 Tilemap
    static Tilemap _ground;
    static Tilemap _hazard;
    static Tilemap _deco;

    // Tile 缓存
    static readonly System.Collections.Generic.Dictionary<string, TileBase> _cache =
        new System.Collections.Generic.Dictionary<string, TileBase>();

    // 实心 Tile：碰撞用整格 Grid（走起来最稳，不会有格子接缝）
    static readonly string[] SolidTileNames =
    {
        "tile_ground_top", "tile_ground_top_left", "tile_ground_top_right",
        "tile_ground_mid", "tile_ground_left", "tile_ground_right",
        "tile_ground_single", "tile_platform", "tile_wall",
    };

    // 尖刺：用 Sprite 轮廓碰撞，这样只有尖的部分会扎人，格子空隙不会
    static readonly string[] SpikeTileNames =
    {
        "tile_spike_up", "tile_spike_down", "tile_spike_left", "tile_spike_right",
    };

    // 纯装饰：不能有碰撞体
    static readonly string[] DecoOnlyTileNames = { "tile_deco_block" };

    [MenuItem("Tools/2D 平台跳跃/生成教学关（一键）", false, 1)]
    public static void BuildTutorialLevel()
    {
        if (!EditorUtility.DisplayDialog(
                "生成教学关 + 难度区 + Boss",
                "会在当前场景里：\n" +
                "  · 删掉所有旧的 Grid / Tilemap\n" +
                "  · 新建 Grid + Ground / Hazard / Background 三层\n" +
                "  · 画出整关（约 782 格宽，20 屏）：教学 → 难度区 → Boss 竞技场\n" +
                "  · 摆好出生点、10 个敌人（普通 / 重甲 / 冲锋）、1 个 Boss、7 个检查点\n" +
                "  · 最后保存场景\n\n" +
                "你自己手画的地形会被覆盖。继续吗？",
                "生成", "取消"))
        {
            return;
        }

        if (!FixTileColliderTypes()) return;
        if (!SetupTilemaps()) return;

        PaintLevel();
        SetupBootstrap();

        // ⚠️ 保存之前必须先把 CompositeCollider2D 的几何体重算出来。
        //
        // 为什么：Ground 层的 TilemapCollider2D 标了 CompositeOperation = Merge，
        // 逐格碰撞体交给 CompositeCollider2D 合并。如果这时候直接保存，
        // 场景里存下的合并结果是空的：
        //     m_CompositePaths: m_Paths: []
        // 编辑器里 Unity 会自动重算所以看不出问题，但【打包后不会】——
        // 逐格碰撞体已经交出去了，合并结果又是空的，整个地面就没有碰撞体，
        // 玩家和敌人会直接穿下去。这个 bug 真的发生过，排查了很久。
        ForceCompositeGeometry();

        // 保存场景 —— 不保存的话这一关只活在内存里
        Scene scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(string.Format(
            "[TutorialLevelBuilder] 教学关生成完毕：宽 {0} 格（{1} 像素），{2}。\n" +
            "  Ground / Hazard / Background 三层已建好，可以直接用 Tile Palette 继续手改。\n" +
            "  出生点在 x=72，敌人 4 个。按 Play 试玩。\n" +
            "  场景保存：{3}",
            LevelWidth, LevelWidth * 16,
            "20 屏：教学(0-10) → 难度区(11-16) → 恢复区(17) → Boss 竞技场(18)",
            saved ? "成功" : "失败（请手动 Ctrl+S）"));
    }

    // ------------------------------------------------------------ 1. 修 Tile 的碰撞类型
    /// <summary>
    /// 强制 Tilemap 和 CompositeCollider2D 把碰撞几何体重算出来（保存场景之前调用）。
    ///
    /// 不这么做的话，场景里存下的 Composite 几何体是空的，
    /// 打包出来的游戏地面会完全没有碰撞 —— 而编辑器里一切正常，非常难查。
    /// </summary>
    static void ForceCompositeGeometry()
    {
        Tilemap[] maps = Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
        for (int i = 0; i < maps.Length; i++)
        {
            maps[i].RefreshAllTiles();
        }

        CompositeCollider2D[] comps = Object.FindObjectsByType<CompositeCollider2D>(FindObjectsSortMode.None);
        for (int i = 0; i < comps.Length; i++)
        {
            var mi = typeof(CompositeCollider2D).GetMethod("GenerateGeometry",
                        System.Type.EmptyTypes);
            if (mi != null)
            {
                mi.Invoke(comps[i], null);
            }
            else
            {
                comps[i].enabled = false;
                comps[i].enabled = true;
            }
        }

        Debug.Log(string.Format(
            "[TutorialLevelBuilder] 已重算碰撞几何体：{0} 个 Tilemap、{1} 个 CompositeCollider2D。",
            maps.Length, comps.Length));
    }

    static bool FixTileColliderTypes()
    {
        int fixedCount = 0;
        var missing = new System.Collections.Generic.List<string>();

        for (int i = 0; i < SolidTileNames.Length; i++)
        {
            if (!SetColliderType(SolidTileNames[i], Tile.ColliderType.Grid, ref fixedCount))
                missing.Add(SolidTileNames[i]);
        }
        for (int i = 0; i < SpikeTileNames.Length; i++)
        {
            if (!SetColliderType(SpikeTileNames[i], Tile.ColliderType.Sprite, ref fixedCount))
                missing.Add(SpikeTileNames[i]);
        }
        for (int i = 0; i < DecoOnlyTileNames.Length; i++)
        {
            if (!SetColliderType(DecoOnlyTileNames[i], Tile.ColliderType.None, ref fixedCount))
                missing.Add(DecoOnlyTileNames[i]);
        }

        if (missing.Count > 0)
        {
            Debug.LogError("[TutorialLevelBuilder] 找不到这些 Tile 资源，无法生成关卡：\n  " +
                           string.Join("\n  ", missing.ToArray()) +
                           "\n请确认它们在 " + TileFolder + " 下（文件名要完全一致）。");
            return false;
        }

        if (fixedCount > 0)
        {
            Debug.Log(string.Format("[TutorialLevelBuilder] 修正了 {0} 个 Tile 的 Collider Type。", fixedCount));
        }
        return true;
    }

    static bool SetColliderType(string tileName, Tile.ColliderType want, ref int fixedCount)
    {
        TileBase tb = AssetDatabase.LoadAssetAtPath<TileBase>(TileFolder + tileName + ".asset");
        if (tb == null) return false;

        Tile t = tb as Tile;
        if (t != null && t.colliderType != want)
        {
            t.colliderType = want;
            EditorUtility.SetDirty(t);
            fixedCount++;
        }
        return true;
    }

    // ------------------------------------------------------------ 2. 建 Grid 和三层 Tilemap
    static bool SetupTilemaps()
    {
        // 删掉旧的 Grid（连同它下面的所有 Tilemap）
        Grid[] oldGrids = Object.FindObjectsByType<Grid>(FindObjectsSortMode.None);
        for (int i = 0; i < oldGrids.Length; i++)
        {
            if (oldGrids[i] != null) Object.DestroyImmediate(oldGrids[i].gameObject);
        }

        var gridGo = new GameObject("Grid");
        var grid = gridGo.AddComponent<Grid>();
        grid.cellSize = new Vector3(1f, 1f, 0f);

        _ground = CreateTilemap(gridGo.transform, "Ground", 0, true, false);
        _hazard = CreateTilemap(gridGo.transform, "Hazard", 0, true, true);
        _deco = CreateTilemap(gridGo.transform, "Background", -10, false, false);

        if (_ground == null || _hazard == null || _deco == null)
        {
            Debug.LogError("[TutorialLevelBuilder] 创建 Tilemap 失败了。");
            return false;
        }
        return true;
    }

    static Tilemap CreateTilemap(Transform parent, string name, int sortingOrder,
                                 bool withCollider, bool isTrigger)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;

        var tm = go.AddComponent<Tilemap>();
        var tr = go.AddComponent<TilemapRenderer>();
        tr.sortingOrder = sortingOrder;

        if (!withCollider) return tm;

        var tc = go.AddComponent<TilemapCollider2D>();
        tc.isTrigger = isTrigger;

        if (isTrigger)
        {
            // 危险区：Trigger + 扣血脚本
            go.AddComponent<HazardZone>();
        }
        else
        {
            // 实心地形：合并成一个连续轮廓。
            // 不合并的话每一格都是独立小方块，角色走在格子接缝上容易卡住。
            var cc = go.AddComponent<CompositeCollider2D>();   // 会自动补 Rigidbody2D
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null) rb.bodyType = RigidbodyType2D.Static;

            cc.geometryType = CompositeCollider2D.GeometryType.Outlines;
            tc.usedByComposite = true;
        }

        return tm;
    }

    // ------------------------------------------------------------ 3. 画关卡
    //
    // 坐标都是格子坐标。地面顶面在 row = MainSurface 时，世界坐标 y = 0。
    // 角色单跳：水平 4.8 格 / 高 3.1 格；二段跳：7.7 格 / 5.3 格；冲刺：10 格左右。
    // 下面所有缺口宽度都是按"留 20% 余量"折算过的。
    //
    static void PaintLevel()
    {
        // ---- 背景装饰：铺一整条带，画在最后面（Order -10），没有碰撞体 ----
        // 行范围要盖住最低的地面（Boss 竞技场在行 -5 下面还有 3 层）
        Deco(-2, LevelWidth + 4, -12, 12);

        // ================================================================
        // S0  x 0..39  开场：全平地，只教"左右走"
        // ================================================================
        Ground(0, 40, MainSurface);

        // ================================================================
        // S1  x 40..79  第一次跳：2 格坑，底部只低 1 格 —— 掉进去能跳出来，不会死
        // ================================================================
        Ground(40, 14, MainSurface);        // 40..53
        Ground(54, 2, MainSurface - 1);     // 坑底（低 1 格）
        Spikes(54, 2, MainSurface);         // 坑底插尖刺：掉进去不死，但会掉血
        Ground(56, 24, MainSurface);        // 56..79

        // ================================================================
        // S2  x 80..119  加压：3 格坑，底部低 2 格 —— 掉下去要费点劲才爬得出来
        // ================================================================
        Ground(80, 16, MainSurface);        // 80..95
        Ground(96, 3, MainSurface - 2);     // 坑底（低 2 格）
        Spikes(96, 3, MainSurface - 1);     // 尖刺
        Ground(99, 21, MainSurface);        // 99..119

        // ================================================================
        // S3  x 120..159  第一个真坑：3 格宽、没有底 —— 掉下去死。教学关的第一次死亡威胁
        //                 宽度必须 ≤ 3.8 格，因为二段跳要到 S4 才教，
        //                 这里只能用单跳过（原来写 4 格，正好卡在单跳没余量的位置）
        // ================================================================
        Ground(120, 16, MainSurface);       // 120..135
        // 136..138 是空的（深渊）
        Ground(139, 21, MainSurface);       // 139..159

        // ================================================================
        // S4  x 160..199  二段跳【教】：4 格高台，单跳（3.1 格）够不到，必须二段跳
        //                 故意没有深坑 —— 失败只是掉回原地重跳，不掉血不死
        // ================================================================
        Ground(160, 40, MainSurface);       // 160..199 整段平地
        Platform(180, 10, MainSurface + 4); // 台面比地面高 4 格 → 必须二段跳
        Platform(184, 2, MainSurface + 5);  // 台子上再垫一小块，做视觉引导

        // ================================================================
        // S5  x 200..239  二段跳【用】：5 格坑 + 对面高 2 格
        // ================================================================
        Ground(200, 16, MainSurface);       // 200..215
        // 216..220 是空的（深渊）
        Ground(221, 19, MainSurface + 2);   // 221..239，高 2 格

        // ================================================================
        // S6  x 240..279  冲刺【教】：8 格大坑。单跳(4.8)和二段跳(7.7)都差一点，
        //                 玩家会自然地把所有按键试一遍，从而发现冲刺
        // ================================================================
        Ground(240, 12, MainSurface);       // 240..251
        // 252..259 是空的（深渊）
        Ground(260, 20, MainSurface);       // 260..279

        // ================================================================
        // S7  x 280..319  冲刺穿人：上层可以绕过去，下层有敌人巡逻
        //                 给两条路 —— 不擅长战斗的玩家也能过
        // ================================================================
        Ground(280, 40, MainSurface);       // 280..319
        Platform(286, 4, MainSurface + 2);  // 上层的台阶
        Platform(292, 16, MainSurface + 4); // 上层走廊（可以跳过下面的敌人）
        // 敌人放在下层，见 SetupBootstrap

        // ================================================================
        // S8  x 320..359  攻击【教】：一条矮走廊，一个敌人堵在中间
        //                 天花板要【很高】，不然玩家二段跳能跳上去、直接绕过整屏
        //                 （二段跳能跳 5.3 格 ≈ 85 像素，所以天花板顶面必须远远高于它）
        // ================================================================
        Ground(320, 40, MainSurface);       // 320..359
        Block(334, 12, MainSurface + 3, MainSurface + 14);  // 从行 3 一直砌到行 14，跳不上去
        // 敌人堵在 340，见 SetupBootstrap

        // ================================================================
        // S9  x 360..399  组合：尖刺 + 深坑 + 敌人，要连着用三个动作
        // ================================================================
        Ground(360, 24, MainSurface);       // 360..383
        Spikes(370, 4, MainSurface + 1);    // 尖刺摆在地面之上
        // 384..387 是空的（深渊）
        Ground(388, 12, MainSurface + 2);   // 388..399，高 2 格

        // ================================================================
        // S10 x 400..439  释放：开阔平台，跑一段，喘口气
        // ================================================================
        Ground(400, 40, MainSurface + 2);   // 400..439，和上一段同高

        // ================================================================
        // ↓↓↓ 从这里开始是【难度区】。基础地面抬高到行 1（= 世界 y 32）↓↓↓
        // ================================================================

        // ================================================================
        // S11 x 440..479  冲刺 + 尖刺：先躲尖刺，再冲过 8 格坑
        // ================================================================
        Ground(440, 12, MainSurface + 2);   // 440..451
        Spikes(446, 4, MainSurface + 3);    // 尖刺就摆在冲刺起跳前
        // 452..459 是空的（深渊，8 格 → 必须冲刺）
        Ground(460, 20, MainSurface + 2);   // 460..479

        // ================================================================
        // S12 x 480..519  精度跳跃：两块窄平台，落脚点只有 4 格宽
        // ================================================================
        Platform(482, 4, MainSurface + 4);  // 482..485，高 3 格
        Platform(492, 6, MainSurface + 5);  // 492..497，再高 1 格（原来只有 4 格宽，
                                            // 导致下一个缺口变成 10 格、正好卡在能力极限上）
        // 498..505 是空的（深渊，8 格 → 冲刺）
        Ground(506, 14, MainSurface + 2);   // 506..519

        // ================================================================
        // S13 x 520..559  天花板尖刺：头顶挂一排，逼你贴地走
        // ================================================================
        Ground(520, 40, MainSurface + 2);   // 520..559
        // 天花板放在行 5~6，尖刺挂在行 4 —— 头顶只剩约 10 像素余量：
        // 走过去没事，但只要跳起来就会扎到。原来尖刺挂太高，不跳就完全没威胁。
        Block(528, 20, MainSurface + 6, MainSurface + 7);
        SpikesDown(534, 8, MainSurface + 5);

        // ================================================================
        // S14 x 560..599  重甲兵：两面尖刺之间塞一个打不退的家伙
        // ================================================================
        Ground(560, 40, MainSurface + 2);   // 560..599
        Spikes(566, 3, MainSurface + 3);
        Spikes(586, 3, MainSurface + 3);
        // 重甲兵在 578，见 SetupBootstrap

        // ================================================================
        // S15 x 600..639  冲锋兵走廊：开阔地，专门给它留出冲撞距离
        //                 （冲锋兵要 110 像素才起手，冲撞 280 像素/秒）
        // ================================================================
        Ground(600, 40, MainSurface + 2);   // 600..639

        // ================================================================
        // S16 x 640..679  混合：窄台 → 8 格坑 → 冲锋兵
        // ================================================================
        Ground(640, 16, MainSurface + 2);   // 640..655
        // 656..663 是空的（深渊，8 格）
        Ground(664, 16, MainSurface + 2);   // 664..679

        // ================================================================
        // S17 x 680..719  恢复区：开阔、无敌人。Boss 战之前的最后一次喘气
        //                 检查点放在 690
        // ================================================================
        Ground(680, 40, MainSurface + 2);   // 680..719

        // ================================================================
        // S18 x 720..779  Boss 竞技场
        //
        // 设计：地面比外面【低 6 格】—— 玩家走出去就掉进来，而二段跳只有 5.3 格，
        //       爬不回去。这样不需要任何"关门"的逻辑，竞技场天然封闭。
        //
        // ⚠️ 行列方向千万别搞反：世界 y = 行号 × 16，所以【行号越大越靠上】。
        //    地面抬高要【加】行号，往下挖要【减】行号。
        //    （这里第一版写成了 MainSurface + 8，等于把竞技场砌到天上去，
        //      玩家走到 x=720 只能看到头顶 96 像素处的一块地面，根本上不去。）
        // ================================================================
        Ground(720, 60, MainSurface - 4);   // 720..779，地面在行 -5（世界 y -64）
        Block(780, 2, MainSurface - 6, MainSurface + 6);    // 右墙，从竞技场地面一直砌到外面高得多
        // Boss 在 750，见 SetupBootstrap

        // ---- 收尾：两侧各立一堵墙，别让玩家跑出关卡 ----
        Block(-2, 2, MainSurface - 2, MainSurface + 12);
        Block(LevelWidth, 2, MainSurface - 2, MainSurface + 12);
    }

    // ------------------------------------------------------------ 画笔

    /// <summary>
    /// 铺一段地面。顶行用带草的 Tile，下面用泥土，两侧换成带边线的版本。
    /// </summary>
    static void Ground(int x0, int width, int surfaceRow, int depth = 3)
    {
        for (int x = x0; x < x0 + width; x++)
        {
            bool left = x == x0;
            bool right = x == x0 + width - 1;

            for (int i = 0; i < depth; i++)
            {
                int row = surfaceRow - i;
                string name;

                if (i == 0)
                {
                    if (left) name = "tile_ground_top_left";
                    else if (right) name = "tile_ground_top_right";
                    else name = "tile_ground_top";
                }
                else
                {
                    if (left) name = "tile_ground_left";
                    else if (right) name = "tile_ground_right";
                    else name = "tile_ground_mid";
                }

                _ground.SetTile(new Vector3Int(x, row, 0), Get(name));
            }
        }
    }

    /// <summary>铺一块实心方块（天花板、墙这种）。不会有草皮。</summary>
    static void Block(int x0, int width, int rowFrom, int rowTo)
    {
        for (int x = x0; x < x0 + width; x++)
        {
            for (int row = rowFrom; row <= rowTo; row++)
            {
                _ground.SetTile(new Vector3Int(x, row, 0), Get("tile_wall"));
            }
        }
    }

    /// <summary>金属平台，整格实心。</summary>
    static void Platform(int x0, int width, int row)
    {
        for (int x = x0; x < x0 + width; x++)
        {
            _ground.SetTile(new Vector3Int(x, row, 0), Get("tile_platform"));
        }
    }

    /// <summary>向上的尖刺（摆在地面上）。</summary>
    static void Spikes(int x0, int width, int row)
    {
        SpikesWith(x0, width, row, "tile_spike_up");
    }

    /// <summary>向下的尖刺（挂在天花板下面）。</summary>
    static void SpikesDown(int x0, int width, int row)
    {
        SpikesWith(x0, width, row, "tile_spike_down");
    }

    static void SpikesWith(int x0, int width, int row, string tileName)
    {
        for (int x = x0; x < x0 + width; x++)
        {
            _hazard.SetTile(new Vector3Int(x, row, 0), Get(tileName));
        }
    }

    /// <summary>背景装饰，铺在 Background 层（Order -10，没有碰撞体）。</summary>
    static void Deco(int x0, int width, int rowFrom, int rowTo)
    {
        for (int x = x0; x < x0 + width; x++)
        {
            for (int row = rowFrom; row <= rowTo; row++)
            {
                _deco.SetTile(new Vector3Int(x, row, 0), Get("tile_deco_block"));
            }
        }
    }

    static TileBase Get(string tileName)
    {
        TileBase cached;
        if (_cache.TryGetValue(tileName, out cached)) return cached;

        TileBase tb = AssetDatabase.LoadAssetAtPath<TileBase>(TileFolder + tileName + ".asset");
        _cache[tileName] = tb;
        return tb;
    }

    // ------------------------------------------------------------ 4. 摆出生点和敌人
    static void SetupBootstrap()
    {
        TestLevelBootstrap bs = Object.FindFirstObjectByType<TestLevelBootstrap>();
        if (bs == null)
        {
            var go = new GameObject("Bootstrap");
            bs = go.AddComponent<TestLevelBootstrap>();
            Debug.Log("[TutorialLevelBuilder] 场景里没有 TestLevelBootstrap，已新建一个 Bootstrap 物体。");
        }

        bs.buildGreyboxPlatforms = false;   // 地形由 Tilemap 提供
        bs.buildKillZone = true;            // 掉进坑里就死
        bs.buildEnemies = true;
        bs.buildBoss = true;
        bs.respawnWhenFallingOut = true;

        // 出生点：S0 平地左边一点。格子 (4, -1) 的顶面是世界 y = 0，所以给 y=24 让它轻轻落下来
        bs.playerSpawn = new Vector2(4 * 16 + 8, 24f);

        // ---- 敌人 ----
        // ⚠️ y 值不能随便改大 —— 上面有平台/天花板的地方，抬高就会把敌人嵌进地形里。
        //    地面在行 -1（顶面 y=0）→ 给 24；地面在行 1（顶面 y=32）→ 给 56。
        bs.enemySpawns = new[]
        {
            // ---- 教学段 ----
            new Vector2(268 * 16 + 8, 24f),   // S6  冲刺坑的落点后面 —— 刚冲完就来一下
            new Vector2(298 * 16 + 8, 88f),   // S7  上层走廊（台面在行 3，顶面 y=64）
            new Vector2(300 * 16 + 8, 24f),   // S7  下层走廊（头顶是平台，只能给 24）
            new Vector2(340 * 16 + 8, 14f),   // S8  矮走廊（重甲 20 像素高，抬高会嵌进天花板）
            new Vector2(392 * 16 + 8, 56f),   // S9  高台
            new Vector2(396 * 16 + 8, 56f),   // S9  第二个
            new Vector2(420 * 16 + 8, 56f),   // S10 收尾
            new Vector2(425 * 16 + 8, 56f),   // S10 重甲收尾

            // ---- 难度段 ----
            new Vector2(470 * 16 + 8, 56f),   // S11 冲锋兵（坑对面，落地就给你一下）
            new Vector2(512 * 16 + 8, 56f),   // S12 普通兵
            new Vector2(545 * 16 + 8, 56f),   // S13 重甲兵（贴着天花板尖刺走）
            new Vector2(578 * 16 + 8, 56f),   // S14 重甲兵（两面尖刺之间）
            new Vector2(620 * 16 + 8, 56f),   // S15 冲锋兵（开阔地，够它冲）
            new Vector2(670 * 16 + 8, 56f),   // S16 冲锋兵（窄台上）
        };

        bs.enemyKinds = new[]
        {
            EnemyPatrol.Kind.Charger,   // S6
            EnemyPatrol.Kind.Grunt,     // S7 上
            EnemyPatrol.Kind.Grunt,     // S7 下
            EnemyPatrol.Kind.Brute,     // S8
            EnemyPatrol.Kind.Grunt,     // S9
            EnemyPatrol.Kind.Grunt,     // S9
            EnemyPatrol.Kind.Grunt,     // S10
            EnemyPatrol.Kind.Brute,     // S10
            EnemyPatrol.Kind.Charger,   // S11
            EnemyPatrol.Kind.Grunt,     // S12
            EnemyPatrol.Kind.Brute,     // S13
            EnemyPatrol.Kind.Brute,     // S14
            EnemyPatrol.Kind.Charger,   // S15
            EnemyPatrol.Kind.Charger,   // S16
        };

        // ---- Boss ----
        // 竞技场地面在行 -5（顶面世界 y = -64），出生给 -40 让它轻轻落下
        bs.bossSpawn = new Vector2(750 * 16 + 8, -40f);
        bs.bossHealth = 20;                // 一般难度的基准值；实际血量由 DifficultySettings 决定
        bs.bossArenaLeftX = 720 * 16;      // 竞技场左边界（像素）
        bs.bossArenaRightX = 779 * 16;     // 右边界

        // ---- 检查点 ----
        // 长关卡必须放：在 Boss 门口死了要从关卡最开头重跑 12000 像素，那是没法玩的。
        // 最后一个特意放在【竞技场里面】—— 死给 Boss 就地重来，不用再跳一次。
        bs.checkpoints = new[]
        {
            new Vector2(80 * 16 + 8, 40f),     // S2  第一次真坑之后（地面行 -1，顶面 y=0）
            new Vector2(205 * 16 + 8, 40f),    // S5  二段跳之后
            new Vector2(330 * 16 + 8, 40f),    // S8  短走廊之前
            new Vector2(405 * 16 + 8, 72f),    // S10 教学段结束（地面行 1，顶面 y=32）
            new Vector2(515 * 16 + 8, 72f),    // S12 难度段开始
            new Vector2(690 * 16 + 8, 72f),    // S17 恢复区（Boss 战之前）
            // S18 竞技场内部。放在【最左端】—— 玩家从 x=720 掉进来正好踩到，
            // 而 Boss 在 750（离这里 26 格）。以前放在 730，Boss 一靠近就会守在复活点旁边，
            // 玩家复活就被再撞一次，反复几次起不来。
            // 另外 GameFlow 复活时还会把 Boss 送回出生点并给玩家 2 秒无敌，两道保险。
            new Vector2(724 * 16 + 8, -40f),
        };

        EditorUtility.SetDirty(bs);

        // 相机边界交给 PlatformerCamera 从 Tilemap 自动算，这里不用填
        Debug.Log(string.Format(
            "[TutorialLevelBuilder] 已摆好：10 个敌人（4 普通 / 2 重甲 / 3 冲锋… 见列表）、" +
            "1 个 Boss（{0} 血）、{1} 个检查点。",
            bs.bossHealth, bs.checkpoints.Length));
    }
}
