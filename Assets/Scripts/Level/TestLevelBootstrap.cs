using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Tilemaps;

/// <summary>
/// 一键生成灰盒测试关。
///
/// 【用法】四步：
///   1. File → New Scene → 选 Empty（空场景）
///   2. 在 Hierarchy 里右键 → Create Empty，改名 Bootstrap
///   3. 把这个脚本拖到 Bootstrap 上
///   4. 按 Play
///
/// 它会在运行时搭出整个测试关：地面、缺口、四级台阶、二段跳高台、右侧墙、死亡区，
/// 并生成玩家、相机、调试面板。
///
/// 【为什么要用运行时生成，而不是给一个 .unity 场景文件】
/// 场景和预制体是二进制/YAML 资源，跨机器复制时很容易出现"引用丢失"，
/// 表现为物体莫名其妙不见了。用代码生成可以把这类问题彻底排除掉：
/// 只要脚本能编译，场景就一定能搭出来。
/// 代价是不能在编辑器里直接拖拽调整——W4 换成 Tilemap 时就会改成正规的场景制作流程。
///
/// 【坐标约定】全部用「像素」，y 轴向上，地面顶面 y = 0。
/// </summary>
[DisallowMultipleComponent]   // 同一个物体上不许挂两次
public class TestLevelBootstrap : MonoBehaviour
{
    [Header("生成内容")]
    [Tooltip("生成灰盒地形（地面、台阶、墙）。用 Tilemap 搭正式关卡时把它取消勾选")]
    public bool buildGreyboxPlatforms = true;
    [Tooltip("生成红色死亡区。正式关卡里一般用 Tilemap 自己画深坑")]
    public bool buildKillZone = true;
    [Tooltip("生成敌人（位置见下面的敌人出生点）")]
    public bool buildEnemies = true;
    [Tooltip("生成 Boss（位置见下面的 Boss 出生点）")]
    public bool buildBoss = false;

    [Tooltip("自动挂上 GameFlow（主菜单 / 暂停 / 死亡 / 通关结算）。" +
             "关掉的话就只有调试面板，没有游戏流程")]
    public bool buildGameFlow = true;

    [Tooltip("自动创建 AudioManager（音效 / BGM）。关掉就完全静音")]
    public bool buildAudio = true;

    [Tooltip("自动创建 AchievementManager（成就统计）")]
    public bool buildAchievements = true;

    [Header("接触伤害判定")]
    [Tooltip("敌人伤害区在【头顶】留出的安全像素。玩家从上面跳过去不会掉血。\n" +
             "副作用是能站在敌人头上 —— 平台游戏里这通常算优点")]
    public int headSafePixels = 4;

    [Header("掉出关卡的保险")]
    [Tooltip("玩家掉到镜头下边界再往下的这个距离（像素）之后，自动送回出生点。" +
             "用 Tilemap 搭关卡、没有画死亡区时，靠它兜底")]
    public bool respawnWhenFallingOut = true;
    public float fallOutMarginPixels = 160f;

    [Header("基准分辨率（像素）")]
    public int referenceWidth = 640;
    public int referenceHeight = 360;

    [Header("玩家出生点（像素）")]
    public Vector2 playerSpawn = new Vector2(60f, 40f);

    [Header("相机边界（像素，世界坐标）")]
    [Tooltip("镜头不会拍到这块矩形之外，避免看到关卡外面的空白")]
    public Vector2 cameraBoundsMin = new Vector2(-260f, -120f);
    public Vector2 cameraBoundsMax = new Vector2(1000f, 420f);

    [Header("灰盒配色")]
    public Color groundColor = new Color(0.22f, 0.26f, 0.34f, 1f);
    public Color wallColor = new Color(0.18f, 0.21f, 0.28f, 1f);
    public Color stepColor = new Color(0.30f, 0.36f, 0.46f, 1f);
    public Color highStepColor = new Color(0.36f, 0.44f, 0.56f, 1f);
    public Color playerColor = new Color(0.36f, 0.78f, 1f, 1f);
    public Color enemyColor = new Color(0.90f, 0.35f, 0.40f, 1f);
    [Tooltip("重甲兵的颜色")]
    public Color bruteColor = new Color(0.62f, 0.40f, 0.85f, 1f);
    [Tooltip("冲锋兵的颜色（注意：它蓄力时会自己变成红色预警）")]
    public Color chargerColor = new Color(0.95f, 0.70f, 0.20f, 1f);
    public Color killZoneColor = new Color(0.80f, 0.20f, 0.20f, 0.25f);
    [Tooltip("Boss 的颜色")]
    public Color bossColor = new Color(0.75f, 0.20f, 0.45f, 1f);
    [Tooltip("检查点的颜色")]
    public Color checkpointColor = new Color(0.55f, 0.60f, 0.72f, 1f);

    [Header("敌人出生点（像素）")]
    [Tooltip("留空就不生成敌人")]
    public Vector2[] enemySpawns =
    {
        new Vector2(470f, 24f),
        new Vector2(820f, 24f),
    };

    [Header("敌人分级（和上面一一对应；短了按 Grunt 处理）")]
    public EnemyPatrol.Kind[] enemyKinds;

    [Header("Boss")]
    [Tooltip("Boss 出生点（像素）。留空或 y<=0 就不生成")]
    public Vector2 bossSpawn = Vector2.zero;
    [Tooltip("Boss 血量。建议 20~40")]
    public int bossHealth = 28;
    [Tooltip("Boss 竞技场左右边界（像素）。0 = 不限制")]
    public float bossArenaLeftX = 0f;
    public float bossArenaRightX = 0f;

    [Header("检查点（像素）")]
    [Tooltip("留空就不生成。长关卡一定要放，不然死了要从头跑")]
    public Vector2[] checkpoints;

    Transform _root;
    KillZone _killZone;
    GameObject _playerGo;
    Vector3 _playerSpawnWorld;
    float _fallLimitWorldY = -9999f;

    void Awake()
    {
        // 防重复：场景里只要已经有一个 TestLevel，就说明挂了不止一个 TestLevelBootstrap
        // （同一个物体挂两次，或者两个物体各挂一个）。那会生成两套重叠的关卡 ——
        // 玩家变两个、敌人叠在一起互相判定成墙、原地抽搐走不动。
        // 这里直接拦住，并给一条能看懂的错误。
        if (GameObject.Find("TestLevel") != null)
        {
            Debug.LogError(
                "[TestLevelBootstrap] 场景里已经有一个 TestLevel 了，说明挂了不止一个 TestLevelBootstrap。" +
                "\n请到 Hierarchy 里检查：选中每个物体，看 Inspector 里是不是有重复的 Test Level Bootstrap 组件，" +
                "把多余的删掉（组件右上角三个点 → Remove Component），只保留一个。本次不再重复生成关卡。", this);
            enabled = false;
            return;
        }

        _root = new GameObject("TestLevel").transform;

        EnsureGlobalLight();

        if (buildGreyboxPlatforms) BuildPlatforms();

        GameObject player = BuildPlayer();
        _playerGo = player;
        _playerSpawnWorld = player.transform.position;

        if (buildKillZone) BuildKillZone(player);

        if (buildEnemies && enemySpawns != null)
        {
            for (int i = 0; i < enemySpawns.Length; i++)
            {
                EnemyPatrol.Kind kind = EnemyPatrol.Kind.Grunt;
                if (enemyKinds != null && i < enemyKinds.Length) kind = enemyKinds[i];

                BuildEnemy("Enemy" + (i + 1) + "_" + kind, enemySpawns[i], kind);
            }
        }

        // 只用 buildBoss 这个开关判断。
        // 以前还额外要求 bossSpawn.y > 0 —— 结果竞技场改成"比外面低 6 格"之后
        // y 是负数，Boss 就再也生成不出来了（而且不报错，很难查）。
        if (buildBoss) BuildBoss();

        if (checkpoints != null)
        {
            for (int i = 0; i < checkpoints.Length; i++)
            {
                BuildCheckpoint("Checkpoint" + (i + 1), checkpoints[i]);
            }
        }

        EnsureCamera(player.transform);

        // 心形血条（左上角）+ 调试面板
        player.AddComponent<HealthHud>();

        DebugHud hud = player.AddComponent<DebugHud>();
        hud.topOffset = 52f;      // 往下让开心形血条那一条

        // ⚠️ 必须在任何东西依赖地面碰撞之前跑。
        //
        // 打包版里 TilemapCollider2D 是死的：格子数据在、组件启用着、合并设置正确，
        // 但它的 bounds 一直是 (0,0,0)，一个形状都不生成；四道强制重建全部无效。
        // 所以这里绕开它，直接从格子生成碰撞体（详见 GridColliderBuilder 的注释）。
        int selfMade = GridColliderBuilder.Build(_root, true, true);
        Debug.Log(string.Format(
            "[TestLevelBootstrap] 自建地面碰撞体：{0} 个（绕开 TilemapCollider2D）", selfMade));

        // 体检放在物理帧之后 —— 碰撞体刚加进物理世界，同帧打射线会打空，得到假阴性
        StartCoroutine(DiagnoseGroundAfterPhysics());

        // 成就统计：要在敌人生成【之前】建好，否则先出生那批敌人登记不上
        if (buildAchievements && FindFirstObjectByType<AchievementManager>() == null)
        {
            gameObject.AddComponent<AchievementManager>();
        }

        // 音频：整个游戏只有这一个入口
        if (buildAudio && FindFirstObjectByType<AudioManager>() == null)
        {
            var audioGo = new GameObject("AudioManager");
            audioGo.transform.SetParent(null);       // 别挂在 TestLevel 下面
            audioGo.AddComponent<AudioManager>();
        }

        // 流程总控：主菜单 / 暂停 / 死亡复活 / 通关结算
        if (buildGameFlow && FindFirstObjectByType<GameFlow>() == null)
        {
            gameObject.AddComponent<GameFlow>();
        }

        Debug.Log("[TestLevelBootstrap] 已生成。\n" +
                  "  地形：" + (buildGreyboxPlatforms ? "灰盒（代码生成）" : "不生成（用 Tilemap 关卡）") + "\n" +
                  "  死亡区：" + (buildKillZone ? "生成" : "不生成") + "\n" +
                  "  敌人：" + (buildEnemies ? "生成" : "不生成") +
                  "（" + (enemySpawns != null ? enemySpawns.Length : 0) + " 个）\n" +
                  "  Boss：" + (buildBoss ? "生成" : "不生成") + "\n" +
                  "  检查点：" + (checkpoints != null ? checkpoints.Length : 0) + " 个\n" +
                  "  音频：" + (buildAudio ? "开启" : "关闭") + "\n" +
                  "操作：WASD 移动，空格跳（长按跳更高），左 Shift 冲刺（无敌+穿人），" +
                  "空中再按空格是二段跳，鼠标左键 / Enter 攻击，E 弹刀，R 回最近的检查点。");
    }

    void Update()
    {
        // 掉出关卡下方就自动送回出生点。
        // 用 Tilemap 搭关卡、没有画死亡区时，这是唯一的兜底 ——
        // 否则玩家会一直往下掉，镜头跟着往下被边界卡住，画面只剩空白。
        if (respawnWhenFallingOut && _playerGo != null)
        {
            if (_playerGo.transform.position.y < _fallLimitWorldY)
            {
                Debug.LogWarning("[TestLevelBootstrap] 玩家掉出关卡了，已自动送回出生点。" +
                                 "如果这不是你想要的，检查「玩家出生点」的坐标是不是还停在旧关卡的数值上。", this);
                RespawnPlayer();
                return;
            }
        }

        // 调试用快捷键：R 直接回出生点。
        // 这里直接读键盘是故意的（不走 Input Action），只为了排查方便。
        Keyboard kb = Keyboard.current;
        if (kb == null || !kb.rKey.wasPressedThisFrame) return;

        if (_killZone != null) _killZone.Respawn();
        else RespawnPlayer();      // 没生成死亡区时（Tilemap 关卡）也要能用
    }

    /// <summary>
    /// 把玩家搬回复活点。有激活过的检查点就用检查点，否则用出生点。
    /// 长关卡没有检查点的话，在 Boss 门口死了要从关卡最开头重跑 —— 那是不可能玩的。
    /// </summary>
    public void RespawnPlayer()
    {
        if (_playerGo == null) return;

        Vector3 target = _playerSpawnWorld;
        Vector3? cp = Checkpoint.RespawnWorldPosition();
        if (cp.HasValue) target = cp.Value;

        var rb = _playerGo.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.position = new Vector2(target.x, target.y);
        }
        _playerGo.transform.position = target;
    }

    // ---------------------------------------------------------------- 灯光
    /// <summary>
    /// URP 2D 的精灵是被 2D 灯光照亮的，场景里一个 Light2D 都没有时会渲染成一片漆黑。
    /// 用反射创建，这样在非 URP 工程里也能编译通过。
    /// </summary>
    void EnsureGlobalLight()
    {
        System.Type lightType = FindType("UnityEngine.Rendering.Universal.Light2D");

        if (lightType == null)
        {
            Debug.Log("[TestLevelBootstrap] 没检测到 URP 的 Light2D（可能不是 URP 工程），跳过灯光检查。");
            return;
        }

        UnityEngine.Object[] existing = UnityEngine.Object.FindObjectsByType(
            lightType, FindObjectsSortMode.None);

        if (existing != null && existing.Length > 0)
        {
            Debug.Log("[TestLevelBootstrap] 场景里已有 2D 灯光，直接沿用。");
            return;
        }

        var go = new GameObject("Global Light 2D");
        go.transform.SetParent(_root, false);

        var light = go.AddComponent(lightType);
        TrySetEnum(light, "lightType", "Global");
        TrySet(light, "color", Color.white);
        TrySet(light, "intensity", 1f);

        Debug.Log("[TestLevelBootstrap] 场景里没有任何 2D 灯光，已自动补一个 Global Light 2D。" +
                  "（URP 2D 的精灵靠灯光照亮，没有灯会一片漆黑。）");
    }

    // ---------------------------------------------------------------- 关卡
    void BuildPlatforms()
    {
        // 起始平台：顶面 y = 0，横跨 x -200 … 700
        Greybox.CreateBox("Ground", new Vector2(250f, -24f), new Vector2(900f, 48f),
                          groundColor, true, _root);

        // 缺口：x 700 … 748，共 48 px = 3 格，用来测跳跃距离
        // 落点平台：x 748 … 892
        Greybox.CreateBox("Landing", new Vector2(820f, -24f), new Vector2(144f, 48f),
                          groundColor, true, _root);

        // 右侧墙：x 892 … 924，y 0 … 160，用来测碰撞
        Greybox.CreateBox("Wall", new Vector2(908f, 80f), new Vector2(32f, 160f),
                          wallColor, true, _root);

        // 四级台阶：每级抬升 36 px，水平间隔 24 px
        Greybox.CreateBox("StepA", new Vector2(300f, 28f), new Vector2(96f, 16f),
                          stepColor, true, _root);   // 顶面 y = 36
        Greybox.CreateBox("StepB", new Vector2(420f, 64f), new Vector2(96f, 16f),
                          stepColor, true, _root);   // 顶面 y = 72
        Greybox.CreateBox("StepC", new Vector2(540f, 100f), new Vector2(96f, 16f),
                          stepColor, true, _root);   // 顶面 y = 108

        // 高台：顶面 y = 180，比上一级高 72 px —— 单跳够不到，必须二段跳
        Greybox.CreateBox("StepD", new Vector2(688f, 172f), new Vector2(96f, 16f),
                          highStepColor, true, _root);
    }

    GameObject BuildPlayer()
    {
        var go = new GameObject("Player");
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(playerSpawn.x / Greybox.PixelsPerUnit,
                                            playerSpawn.y / Greybox.PixelsPerUnit,
                                            0f);

        var sr = go.AddComponent<SpriteRenderer>();
        // 有正式精灵就用，没有就退回纯色方块 —— 美术没到位不该让游戏跑不起来
        if (!TryMakePlayerVisual(go, sr)) sr.sprite = Greybox.Solid(playerColor, 12, 22);
        sr.sortingOrder = 10;

        var bc = go.AddComponent<BoxCollider2D>();
        bc.size = new Vector2(12f / Greybox.PixelsPerUnit, 22f / Greybox.PixelsPerUnit);

        var rb = go.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        // Health 必须在 PlayerController 之前加 —— 组件是运行时创建的，
        // PlayerController 的懒加载虽然能兜住，但先加更符合直觉
        var health = go.AddComponent<Health>();
        health.maxHealth = 3;
        health.currentHealth = 3;
        health.invincibleTime = 0.8f;

        go.AddComponent<PlayerController>();
        go.AddComponent<PlayerCombat>();
        go.AddComponent<LandingDust>();
        return go;
    }

    GameObject BuildEnemy(string name, Vector2 posPixels, EnemyPatrol.Kind kind)
    {
        // headSafePixels 是实例字段（可在 Inspector 调），这里显式传给静态工厂
        return SpawnEnemy(name, posPixels, kind, _root, enemyColor, bruteColor, chargerColor,
                          false, headSafePixels);
    }

    // ---------------------------------------------------------------- 角色美术
    //
    // 设计：Resources/Art 下有正式精灵就用，没有就退回纯色方块。
    // 这样"美术没到位"永远只是"看起来朴素"，不会变成"游戏跑不起来"。
    // 占位精灵由 build/make_characters.py 生成。

    const string ArtFolder = "Art";
    const float ExpectPpu = 16f;

    static void WarnIfWrongPpu(Sprite s, string who)
    {
        if (s == null) return;
        if (Mathf.Approximately(s.pixelsPerUnit, ExpectPpu)) return;

        Debug.LogWarning(string.Format(
            "[TestLevelBootstrap] {0} 的精灵 Pixels Per Unit = {1:0}，应该是 {2:0} —— 角色会明显偏大或偏小。\n" +
            "修法：在 Project 窗口里选中 Assets/Resources/Art 下的所有 png，" +
            "Inspector 里把 Pixels Per Unit 改成 {2:0}、Filter Mode 改成 Point，然后 Apply。",
            who, s.pixelsPerUnit, ExpectPpu));
    }

    /// <summary>
    /// 强制重算 CompositeCollider2D 的几何体。
    ///
    /// 【这个 bug 的全貌】
    /// 关卡的 Ground 层用了 TilemapCollider2D + CompositeCollider2D 的标准组合，
    /// 并且 TilemapCollider2D 标了 CompositeOperation = Merge ——
    /// 意思是"我的逐格碰撞体不要了，交给 CompositeCollider2D 合并成整体"。
    ///
    /// 而场景文件里 CompositeCollider2D 缓存下来的几何体是空的：
    ///     m_ColliderPaths: [... m_ColliderPaths: []]
    ///     m_CompositePaths: m_Paths: []
    ///
    /// 编辑器里没关系，Unity 会自动重算。但【打包后不会】——
    /// 逐格碰撞体已经交出去了，合并结果又是空的，于是整个地面一个碰撞体都没有。
    ///
    /// 表现就是：地面画得好好的（渲染和碰撞是两套系统），
    /// 镜头边界也对（PlatformerCamera 用的是 TilemapRenderer.bounds 渲染范围，和碰撞无关），
    /// 但玩家和敌人全部穿过去往下掉。
    ///
    /// ⚠️ 注意：这一段是当初的【假设】，后来证明它【不是】根因 ——
    /// 强制重算之后地面依然不碰撞（见 Player.log）。保留这段代码是因为它本身是
    /// 正确的加固措施（空缓存的合并结果确实该重算），但真相还没找到。
    /// 真正的排查靠下面的 DiagnoseGroundCollision()。
    /// </summary>
    static void ForceCompositeRegeneration()
    {
        int tilemaps = 0;
        int composites = 0;

        // 先让 Tilemap 重新上报一遍格子 —— 这是最可靠的中转信号，
        // 会连带把 TilemapCollider2D 标脏，进而触发 Composite 重新合并。
        Tilemap[] maps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
        for (int i = 0; i < maps.Length; i++)
        {
            maps[i].RefreshAllTiles();
            tilemaps++;
        }

        // 再显式要求 Composite 重算一次。
        CompositeCollider2D[] comps = FindObjectsByType<CompositeCollider2D>(FindObjectsSortMode.None);
        for (int i = 0; i < comps.Length; i++)
        {
            CompositeCollider2D c = comps[i];
            composites++;

            // GenerateGeometry() 是较新版本才有的公开 API，用反射调，老版本没有也不会报错
            var mi = typeof(CompositeCollider2D).GetMethod("GenerateGeometry",
                        System.Type.EmptyTypes);
            if (mi != null)
            {
                mi.Invoke(c, null);
                continue;
            }

            // 退回方案：开关一次。重新启用会强制重新合并一次
            c.enabled = false;
            c.enabled = true;
        }

        Debug.Log(string.Format(
            "[TestLevelBootstrap] 已强制重算碰撞几何体：{0} 个 Tilemap、{1} 个 CompositeCollider2D。" +
            "（打包版地面站不上去就是这里修掉的：场景里缓存的合并结果是空的，运行时必须重算）",
            tilemaps, composites));
    }

    /// <summary>等物理帧之后体检，确认自建碰撞体真的进了物理世界</summary>
    System.Collections.IEnumerator DiagnoseGroundAfterPhysics()
    {
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        DiagnoseGroundCollision(playerSpawn, Greybox.PixelsPerUnit, "自建碰撞体之后");
    }

    /// <summary>
    /// 强制 TilemapCollider2D 重新生成碰撞形状。四道保险，从温和到强硬。
    ///
    /// 背景：打包版里 TilemapCollider2D 的 bounds 是 (0,0,0) —— 它一个形状都没生成，
    /// 尽管格子上确实有 2466 个 colliderType=Grid 的 Tile。
    /// 而且 merge=None 的尖刺层也一样是零，所以和 Composite 无关，
    /// 就是 TilemapCollider2D 自己没建。
    ///
    /// 猜测是"脏标记"的问题：碰撞形状是运行时生成的，不进场景文件；
    /// 如果加载时内部状态认为"没有变化待处理"，就不会重建。
    /// 编辑器里 Unity 自己会刷新所以看不出来，打包后不会。
    ///
    /// 所以这里按【从温和到强硬】的顺序挨个试，哪一道生效都行：
    ///   1. ProcessTilemapChanges()  —— 官方给的强制重建入口（老版本有）
    ///   2. SetTile 一次             —— 真正把 Tilemap 标脏，这是最可靠的一道
    ///   3. RefreshAllTiles()        —— 重新加载所有格子
    ///   4. Composite 重算 / 开关一次
    /// </summary>
    static void ForceGroundColliderRebuild()
    {
        Tilemap[] maps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);

        for (int i = 0; i < maps.Length; i++)
        {
            Tilemap map = maps[i];
            TilemapCollider2D tc = map.GetComponent<TilemapCollider2D>();

            if (tc == null)
            {
                // 纯背景层，没有碰撞体，跳过
                map.RefreshAllTiles();
                continue;
            }

            // 1. 官方的强制重建入口
            var process = typeof(TilemapCollider2D).GetMethod("ProcessTilemapChanges",
                              System.Type.EmptyTypes);
            if (process != null)
            {
                process.Invoke(tc, null);
            }

            // 2. 把任意一个有格子的位置重设一遍同一个 Tile。
            //    这是最关键的一道 —— RefreshAllTiles 不一定会把 TilemapCollider2D 标脏，
            //    但 SetTile 一定会。只动一个格子，代价可以忽略。
            TileBase anyTile = null;
            Vector3Int anyPos = Vector3Int.zero;
            foreach (Vector3Int pos in map.cellBounds.allPositionsWithin)
            {
                TileBase tb = map.GetTile(pos);
                if (tb == null) continue;
                anyTile = tb;
                anyPos = pos;
                break;
            }
            if (anyTile != null) map.SetTile(anyPos, anyTile);

            // 3. 重新加载所有格子
            map.RefreshAllTiles();

            // 4. Composite
            CompositeCollider2D cc = map.GetComponent<CompositeCollider2D>();
            if (cc != null)
            {
                var gen = typeof(CompositeCollider2D).GetMethod("GenerateGeometry",
                              System.Type.EmptyTypes);
                if (gen != null) gen.Invoke(cc, null);
                else { cc.enabled = false; cc.enabled = true; }
            }
        }

        Debug.Log("[TestLevelBootstrap] 已尝试强制重建地面碰撞（ProcessTilemapChanges → SetTile → RefreshAllTiles → GenerateGeometry）");
    }

    /// <summary>
    /// 地面碰撞体检。"打包版地面站不上去"排查用。
    ///
    /// 之前我靠读场景文件排查：Tile 的 ColliderType、Composite 的合并设置、
    /// 图层碰撞矩阵、GameObject 从属关系 —— 全都核过一遍，全都正常，但打包版就是不碰。
    /// 静态分析到头了，只能让游戏在【运行时】把实况打出来：
    /// 碰撞体到底存不存在、几何体有多大、从出生点往下打射线能打到什么。
    /// </summary>
    static void DiagnoseGroundCollision(Vector2 spawnPx, float ppu, string phase)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format("========== 地面碰撞体检（{0}）==========", phase));

        // ---- 1. Tilemap 层 ----
        Tilemap[] maps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
        sb.AppendLine(string.Format("Tilemap 共 {0} 个：", maps.Length));
        for (int i = 0; i < maps.Length; i++)
        {
            Tilemap t = maps[i];
            TilemapCollider2D tc = t.GetComponent<TilemapCollider2D>();
            CompositeCollider2D cc = t.GetComponent<CompositeCollider2D>();
            Rigidbody2D rb = t.GetComponent<Rigidbody2D>();

            // GetUsedTilesCount() = 这个 Tilemap 上真正用到的不同 Tile 种类数。
            // 如果是 0，说明格子数据压根没加载进来 —— 那就不是碰撞问题，是关卡没了。
            int usedTiles = -1;
            try { usedTiles = t.GetUsedTilesCount(); }
            catch { usedTiles = -2; }

            // ⭐ 最关键的一项：运行时这些格子上到底是什么 Tile、它的 ColliderType 是什么。
            // 磁盘上的 tile .asset 写着 Grid（整格实心），但如果打包版加载出来的不是这个值，
            // 那"地面没有碰撞体"就有解释了 —— 而且这是静态分析永远看不到的。
            var kindCount = new Dictionary<string, int>();
            int tileCells = 0;
            try
            {
                foreach (Vector3Int pos in t.cellBounds.allPositionsWithin)
                {
                    TileBase tb = t.GetTile(pos);
                    if (tb == null) continue;
                    tileCells++;

                    var tile = tb as Tile;
                    string key = tile != null
                        ? (tb.GetType().Name + ".colliderType=" + tile.colliderType)
                        : tb.GetType().Name;
                    int n;
                    kindCount[key] = kindCount.TryGetValue(key, out n) ? n + 1 : 1;
                }
            }
            catch (System.Exception e)
            {
                kindCount["读取失败: " + e.GetType().Name] = 1;
            }

            var kinds = new System.Text.StringBuilder();
            foreach (var kv in kindCount) kinds.Append(kv.Key + " ×" + kv.Value + "; ");

            sb.AppendLine(string.Format(
                "  [{0}] 用到Tile种类={1} 格子数={2}\n" +
                "       ★格子上的Tile: {3}\n" +
                "       cellBounds={4}\n" +
                "       TilemapCollider2D={5}\n" +
                "       CompositeCollider2D={6}\n" +
                "       Rigidbody2D={7}",
                t.name, usedTiles, tileCells,
                kinds.Length > 0 ? kinds.ToString() : "（一个格子都没有！）",
                t.cellBounds,
                tc == null ? "【无】" : string.Format("有 enabled={0} trigger={1} merge={2} bounds={3}",
                    tc.enabled, tc.isTrigger, tc.compositeOperation, tc.bounds),
                cc == null ? "【无】" : string.Format("有 enabled={0} 形状数={1} bounds={2}",
                    cc.enabled, cc.shapeCount, cc.bounds),
                rb == null ? "【无】" : string.Format("有 type={0} simulated={1}", rb.bodyType, rb.simulated)));
        }

        // ---- 2. 从出生点向下打射线 ----
        // ⚠️ 必须排除玩家自己。Physics2D 的 QueriesStartInColliders 默认是开的，
        // 射线从玩家体内出发会【在距离 0 处打到玩家】并就此停住 ——
        // 上一版诊断就是这么废掉的：报告"打到了 Player"，却看不出地面有没有碰撞体。
        Vector2 origin = spawnPx / ppu;
        sb.AppendLine(string.Format("从出生点 {0} 向下打射线 8 单位（已排除玩家自身）：", origin));

        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, Vector2.down, 8f);
        int solidHits = 0;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null) continue;
            if (c.GetComponentInParent<PlayerController>() != null) continue;   // 跳过玩家

            solidHits++;
            sb.AppendLine(string.Format("  ✓ 打到 {0}（{1}，距离 {2:0.00}，图层 {3}，Trigger={4}）",
                c.name, c.GetType().Name, hits[i].distance, c.gameObject.layer, c.isTrigger));
        }
        if (solidHits == 0)
        {
            sb.AppendLine("  ✗ 除玩家外什么都没打到 —— 地面【确实没有碰撞体】");
        }

        // ---- 3. 出生点附近直接找一遍（同样排除玩家）----
        Collider2D[] near = Physics2D.OverlapCircleAll(origin, 0.5f);
        int solidNear = 0;
        var nearText = new System.Text.StringBuilder();
        for (int i = 0; i < near.Length; i++)
        {
            if (near[i].GetComponentInParent<PlayerController>() != null) continue;
            solidNear++;
            nearText.AppendLine(string.Format("  · {0}（{1}，图层 {2}）",
                near[i].name, near[i].GetType().Name, near[i].gameObject.layer));
        }
        sb.AppendLine(string.Format("出生点半径 0.5 内、排除玩家后的碰撞体：{0} 个", solidNear));
        if (solidNear > 0) sb.Append(nearText);

        // ---- 4. 图层矩阵 ----
        sb.AppendLine(string.Format("Physics2D.GetIgnoreLayerCollision(0,0) = {0}（false 才对）",
            Physics2D.GetIgnoreLayerCollision(0, 0)));

        Debug.Log(sb.ToString());
    }

    /// <summary>玩家：有 hero_* 精灵就挂状态驱动的动画，否则返回 false 让调用方用方块</summary>
    static bool TryMakePlayerVisual(GameObject go, SpriteRenderer sr)
    {
        Sprite idle0 = Resources.Load<Sprite>(ArtFolder + "/hero_idle_0");
        if (idle0 == null) return false;

        sr.sprite = idle0;
        WarnIfWrongPpu(idle0, "主角");

        var anim = go.AddComponent<SpriteAnimator>();
        anim.driveFromPlayerState = true;

        anim.AddClip("idle", 4f, true, SpriteAnimator.LoadFrames(ArtFolder, "hero_idle_", 2));
        anim.AddClip("run", 12f, true, SpriteAnimator.LoadFrames(ArtFolder, "hero_run_", 4));
        anim.AddClip("jump", 1f, false, SpriteAnimator.LoadFrames(ArtFolder, "hero_jump_", 1));
        anim.AddClip("fall", 1f, false, SpriteAnimator.LoadFrames(ArtFolder, "hero_fall_", 1));
        anim.AddClip("hurt", 1f, false, SpriteAnimator.LoadFrames(ArtFolder, "hero_hurt_", 1));
        // 冲刺没做单独动画 —— 会沿用上一段（通常是 run），能接受

        return true;
    }

    /// <summary>敌人：两帧摇摆。前摇 / 追击的变色仍由 EnemyPatrol 自己管。</summary>
    static bool TryMakeEnemyVisual(GameObject go, SpriteRenderer sr, EnemyPatrol.Kind kind)
    {
        string prefix;
        switch (kind)
        {
            case EnemyPatrol.Kind.Brute: prefix = "enemy_brute_"; break;
            case EnemyPatrol.Kind.Charger: prefix = "enemy_charger_"; break;
            default: prefix = "enemy_grunt_"; break;
        }

        Sprite f0 = Resources.Load<Sprite>(ArtFolder + "/" + prefix + "0");
        if (f0 == null) return false;

        sr.sprite = f0;

        var anim = go.AddComponent<SpriteAnimator>();
        anim.AddClip("idle", 3.5f, true, SpriteAnimator.LoadFrames(ArtFolder, prefix, 2));
        return true;
    }

    /// <summary>Boss：两帧，更慢更沉</summary>
    static bool TryMakeBossVisual(GameObject go, SpriteRenderer sr)
    {
        Sprite f0 = Resources.Load<Sprite>(ArtFolder + "/boss_0");
        if (f0 == null) return false;

        sr.sprite = f0;
        WarnIfWrongPpu(f0, "Boss");

        var anim = go.AddComponent<SpriteAnimator>();
        anim.AddClip("idle", 2f, true, SpriteAnimator.LoadFrames(ArtFolder, "boss_", 2));
        return true;
    }

    /// <summary>
    /// 敌人工厂。做成 public static 是为了让 Boss 召唤小兵时能复用同一套代码 ——
    /// 不然 Boss 那边得自己复制一份"两个碰撞体 + Rigidbody + Health"的搭建逻辑，
    /// 一旦哪边忘了改（比如又漏了 SleepMode）就会出很隐蔽的 bug。
    /// </summary>
    public static GameObject SpawnEnemy(string name, Vector2 posPixels, EnemyPatrol.Kind kind,
                                        Transform parent, Color baseColor,
                                        Color bruteColor = default, Color chargerColor = default,
                                        bool summoned = false, int headSafe = 4)
    {
        const float ppu = Greybox.PixelsPerUnit;

        // 不同分级的体型 / 血量 / 颜色
        int sizePx = kind == EnemyPatrol.Kind.Brute ? 20 : 14;
        int triggerPx = sizePx + 4;

        int maxHp = 2;
        if (kind == EnemyPatrol.Kind.Brute) maxHp = 7;
        else if (kind == EnemyPatrol.Kind.Charger) maxHp = 4;

        Color color = baseColor;
        if (kind == EnemyPatrol.Kind.Brute && bruteColor.a > 0f) color = bruteColor;
        else if (kind == EnemyPatrol.Kind.Charger && chargerColor.a > 0f) color = chargerColor;

        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(posPixels.x / ppu, posPixels.y / ppu, 0f);

        var sr = go.AddComponent<SpriteRenderer>();
        if (!TryMakeEnemyVisual(go, sr, kind)) sr.sprite = Greybox.Solid(color, sizePx, sizePx);
        sr.sortingOrder = 8;

        // 两个碰撞体：实心的（站地面、挡玩家）+ Trigger（扣玩家的血）
        var solid = go.AddComponent<BoxCollider2D>();
        solid.size = new Vector2(sizePx / ppu, sizePx / ppu);

        // ---- 伤害判定区：头顶必须留安全区 ----
        // 之前是 sizePx+4 的【正方形居中】，比身体上下各高出 2 像素 ——
        // 结果玩家从敌人头顶跳过去也会掉血，非常反直觉。
        // 现在：宽度仍然比身体宽一点（擦边也算撞到），但高度只盖住下半身，
        // 头顶留出安全区。副作用是"能站在敌人头上"，这是好事。
        //
        // ⚠️ headSafe 是【参数】不是直接读 headSafePixels ——
        //    这个方法是 static（Boss 召唤小兵要复用），静态方法读不到实例字段，
        //    直接写会报 CS0120。调用方自己把值传进来。
        int dmgH = Mathf.Max(4, sizePx - headSafe);
        var trigger = go.AddComponent<BoxCollider2D>();
        trigger.size = new Vector2(triggerPx / ppu, dmgH / ppu);
        trigger.offset = new Vector2(0f, -headSafe * 0.5f / ppu);
        trigger.isTrigger = true;

        var rb = go.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;          // 全局重力是 0，敌人自己施加（EnemyPatrol.Awake 里）
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

        var health = go.AddComponent<Health>();
        health.maxHealth = maxHp;
        health.currentHealth = maxHp;
        health.invincibleTime = 0f;    // 敌人没有无敌帧，每次挥砍都能打中

        var patrol = go.AddComponent<EnemyPatrol>();
        patrol.kind = kind;

        switch (kind)
        {
            case EnemyPatrol.Kind.Brute:
                patrol.moveSpeed = 26f;
                patrol.chaseSpeed = 70f;
                patrol.contactDamage = 2;
                patrol.ignoreKnockback = true;      // 打不退
                patrol.contactDamageInterval = 1.0f;
                break;

            case EnemyPatrol.Kind.Charger:
                patrol.moveSpeed = 34f;
                patrol.chaseSpeed = 78f;
                patrol.contactDamage = 2;
                patrol.staggerTime = 0.25f;
                patrol.contactDamageInterval = 0.9f;
                break;

            default:
                break;
        }

        // 配置完了再记基准值 —— 难度系统后面要拿它重新算。
        // 必须放在 switch 之后，否则记下的是默认值而不是这个分级该有的速度。
        patrol.RecordBaseStats();

        // 是不是 Boss 召唤出来的。成就的"不杀 / 杀光"要排除这些 ——
        // 二阶段会无限召唤，算进去的话"杀光"永远不可能达成。
        patrol.isSummoned = summoned;

        // 登记到成就统计（召唤的不算）
        if (!summoned) AchievementManager.NotifyEnemySpawned(patrol);

        // 敌人之间不要互相挡路（见下面 IgnoreCharacterCollisions 的说明）
        IgnoreCharacterCollisions(go, solid);

        return go;
    }

    /// <summary>
    /// 让敌人之间、以及敌人和 Boss 之间【不产生物理碰撞】。
    ///
    /// 【为什么必须这么做】EnemyPatrol.HitsSomething() 会跳过"其他有 Health 的碰撞体"，
    /// 也就是敌人【逻辑上】不把彼此当墙 —— 这是为了不让它们一靠近就互相判定成墙、原地抽搐。
    /// 但物理层它们仍然是两个实心的 Dynamic 刚体：一旦撞上就会互相顶住，
    /// 各自还想往前走，于是永远磨在一起。
    ///
    /// 表现就是日志里那种：位移 8px（期望 47px）、vx 只有 0.5（本该 2.9）、
    /// groundAhead 和 wallAhead 都是 false，掉头 0 次 —— 什么都正常，就是走不动。
    ///
    /// 交互界面（接触伤害）走的是 Trigger，IgnoreCollision 不影响 Trigger 判定吗？
    /// 会影响 —— 但敌人之间本来就不该互相造成伤害，所以正合适。
    /// </summary>
    static void IgnoreCharacterCollisions(GameObject self, Collider2D solid)
    {
        if (solid == null) return;

        EnemyPatrol[] others = Object.FindObjectsByType<EnemyPatrol>(FindObjectsSortMode.None);
        for (int i = 0; i < others.Length; i++)
        {
            if (others[i] == null) continue;
            if (others[i].gameObject == self) continue;

            Collider2D[] cols = others[i].GetComponents<Collider2D>();
            for (int c = 0; c < cols.Length; c++)
            {
                if (cols[c] == null || cols[c] == solid) continue;
                Physics2D.IgnoreCollision(solid, cols[c], true);
            }
        }

        // Boss 也不能被小兵顶住（二阶段会召唤小兵，正好站在 Boss 身上）
        BossController boss = Object.FindFirstObjectByType<BossController>();
        if (boss != null)
        {
            Collider2D[] bossCols = boss.GetComponents<Collider2D>();
            for (int c = 0; c < bossCols.Length; c++)
            {
                if (bossCols[c] == null || bossCols[c] == solid) continue;
                Physics2D.IgnoreCollision(solid, bossCols[c], true);
            }
        }
    }

    // ---------------------------------------------------------------- Boss 与检查点
    void BuildBoss()
    {
        const float ppu = Greybox.PixelsPerUnit;

        var go = new GameObject("Boss");
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(bossSpawn.x / ppu, bossSpawn.y / ppu, 0f);

        var sr = go.AddComponent<SpriteRenderer>();
        if (!TryMakeBossVisual(go, sr)) sr.sprite = Greybox.Solid(bossColor, 34, 34);
        sr.sortingOrder = 9;

        var solid = go.AddComponent<BoxCollider2D>();
        solid.size = new Vector2(34f / ppu, 34f / ppu);

        // Boss 的判定区同样去掉头顶一段（默认 6 像素，它个头大，安全区也给宽一点）
        const int bossHeadSafePx = 6;
        var trigger = go.AddComponent<BoxCollider2D>();
        trigger.size = new Vector2(40f / ppu, (34 - bossHeadSafePx) / ppu);
        trigger.offset = new Vector2(0f, -bossHeadSafePx * 0.5f / ppu);
        trigger.isTrigger = true;

        var rb = go.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

        var health = go.AddComponent<Health>();
        health.maxHealth = bossHealth;
        health.currentHealth = bossHealth;
        health.invincibleTime = 0f;

        var boss = go.AddComponent<BossController>();
        boss.arenaLeftX = bossArenaLeftX;
        boss.arenaRightX = bossArenaRightX;

        Debug.Log(string.Format("[TestLevelBootstrap] Boss 已生成：血量 {0}，竞技场 x {1:0} ~ {2:0} 像素",
                  bossHealth, bossArenaLeftX, bossArenaRightX));
    }

    void BuildCheckpoint(string name, Vector2 posPixels)
    {
        const float ppu = Greybox.PixelsPerUnit;

        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(posPixels.x / ppu, posPixels.y / ppu, 0f);

        // 柱子
        var pillar = new GameObject("Pillar");
        pillar.transform.SetParent(go.transform, false);
        pillar.transform.localPosition = new Vector3(0f, 12f / ppu, 0f);

        var sr = pillar.AddComponent<SpriteRenderer>();
        sr.sprite = Greybox.Solid(checkpointColor, 6, 32);
        sr.sortingOrder = 6;

        // 触发区
        var bc = go.AddComponent<BoxCollider2D>();
        bc.size = new Vector2(20f / ppu, 48f / ppu);
        bc.isTrigger = true;

        go.AddComponent<Checkpoint>();
    }

    void BuildKillZone(GameObject player)    {
        var go = new GameObject("KillZone");
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(400f / Greybox.PixelsPerUnit,
                                            -320f / Greybox.PixelsPerUnit, 0f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Greybox.Solid(killZoneColor, 1600, 40);
        sr.sortingOrder = -5;

        var bc = go.AddComponent<BoxCollider2D>();
        bc.size = new Vector2(1600f / Greybox.PixelsPerUnit, 40f / Greybox.PixelsPerUnit);
        bc.isTrigger = true;

        _killZone = go.AddComponent<KillZone>();
        _killZone.target = player.transform;
    }

    // ---------------------------------------------------------------- 相机
    void EnsureCamera(Transform target)
    {
        // 直接复用场景里已有的 Main Camera（它已经带好 AudioListener），没有才新建
        Camera cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }

        // 相机不能挂在玩家下面 —— 子物体会被父物体硬拽着走，就没法做平滑跟随了
        cam.transform.SetParent(null, true);
        cam.transform.position = new Vector3(target.position.x, target.position.y, -10f);
        cam.orthographic = true;
        cam.orthographicSize = referenceHeight / 2f / Greybox.PixelsPerUnit;   // 360/2/16 = 11.25
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.09f, 0.10f, 0.13f, 1f);
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;

        TryAddPixelPerfectCamera(cam.gameObject);

        // 挂上跟随脚本，并把关卡边界告诉它
        var follow = cam.GetComponent<PlatformerCamera>();
        if (follow == null)
        {
            follow = cam.gameObject.AddComponent<PlatformerCamera>();
        }
        follow.target = target;
        follow.useBounds = true;

        // 只有"不用自动边界"时才把手填值写进去。
        // 注意顺序陷阱：上面 AddComponent 的那一刻，PlatformerCamera.Awake() 就已经跑过、
        // 并且已经从 Tilemap 算好了边界。这里要是无条件覆盖，自动算的结果就白费了 ——
        // 镜头会一直用 Bootstrap 里那套旧灰盒坐标，表现就是"镜头不动 / 拍到关卡外面"。
        if (!follow.autoBoundsFromTilemap)
        {
            follow.boundsMin = cameraBoundsMin;
            follow.boundsMax = cameraBoundsMax;
            Debug.Log("[TestLevelBootstrap] 使用了 Inspector 里手填的镜头边界。");
        }
        else
        {
            Debug.Log(string.Format(
                "[TestLevelBootstrap] 镜头边界用自动计算的（当前值：({0:0},{1:0}) 到 ({2:0},{3:0}) 像素）。" +
                "如果不合适，取消 Main Camera 上 Platformer Camera 的 Auto Bounds From Tilemap，" +
                "再回这里手填。",
                follow.boundsMin.x, follow.boundsMin.y, follow.boundsMax.x, follow.boundsMax.y));
        }

        // 把镜头的下边界记下来，做"掉出关卡自动送回出生点"的保险
        _fallLimitWorldY = follow.boundsMin.y / PlayerController.PixelsPerUnit
                          - fallOutMarginPixels / PlayerController.PixelsPerUnit;

        Debug.Log(string.Format(
            "[TestLevelBootstrap] 相机已改为平滑跟随。掉出关卡下方 {0:0} 像素会自动送回出生点（阈值 y = {1:0.0} 单位）",
            fallOutMarginPixels, _fallLimitWorldY));
    }

    /// <summary>
    /// 按类型全名找 URP 的类型。
    ///
    /// 为什么不能只写 System.Type.GetType("全名, 程序集名")：
    /// 那个写法要求程序集【已经被加载】，而且名字要完全对得上。URP 改过好几次程序集名，
    /// 一旦对不上就静默返回 null —— 你会看到"没检测到 URP 的 PixelPerfectCamera"，
    /// 但其实 URP 装得好好的。所以这里退一步：在所有【已加载】的程序集里按全名找。
    /// </summary>
    static System.Type FindType(string fullName)
    {
        // 先试最常见的写法（最快）
        System.Type t = System.Type.GetType(
            fullName + ", Unity.RenderPipelines.Universal.Runtime");
        if (t != null) return t;

        // 再在所有已加载程序集里找，不依赖程序集名字
        System.Reflection.Assembly[] assemblies =
            System.AppDomain.CurrentDomain.GetAssemblies();

        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                t = assemblies[i].GetType(fullName, false);
                if (t != null) return t;
            }
            catch (System.Exception)
            {
                // 个别程序集反射会抛异常，跳过就行
            }
        }

        return null;
    }

    /// <summary>
    /// 自动挂上 URP 的 Pixel Perfect Camera。用反射是为了在没用 URP 的工程里也能编译通过。
    /// 挂不上也不影响游戏，只是画面边缘可能出现非整数倍缩放。
    /// </summary>
    void TryAddPixelPerfectCamera(GameObject camObject)
    {
        System.Type type = FindType("UnityEngine.Rendering.Universal.PixelPerfectCamera");

        if (type == null)
        {
            Debug.LogWarning("[TestLevelBootstrap] 没找到 URP 的 PixelPerfectCamera 类型。" +
                      "如果你确定工程用的是 URP，请手动给 Main Camera 添加 Pixel Perfect Camera 组件并设置：" +
                      "Assets Pixels Per Unit = 16、Reference Resolution = 640×360、" +
                      "Grid Snapping = Upscale Render Texture。");
            return;
        }

        try
        {
            if (camObject.GetComponent(type) == null)
            {
                camObject.AddComponent(type);
            }
            var comp = camObject.GetComponent(type);

            TrySet(comp, "assetsPPU", 16);
            TrySet(comp, "refResolutionX", referenceWidth);
            TrySet(comp, "refResolutionY", referenceHeight);
            TrySetEnum(comp, "gridSnapping", "UpscaleRenderTexture");
            TrySet(comp, "upscaleRT", true);        // 旧版 URP 的布尔属性名
            TrySet(comp, "pixelSnapping", true);    // 旧版 URP 的布尔属性名
            TrySet(comp, "cropFrameX", false);
            TrySet(comp, "cropFrameY", false);

            Debug.Log("[TestLevelBootstrap] 已自动配置 URP Pixel Perfect Camera（Assets Pixels Per Unit 16 / 640×360）。");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[TestLevelBootstrap] 自动配置 PixelPerfectCamera 失败：" + e.Message +
                             "\n已退回普通正交相机，不影响玩法。");
        }
    }

    static void TrySet(object target, string propertyName, object value)
    {
        var p = target.GetType().GetProperty(propertyName);
        if (p != null && p.CanWrite)
        {
            p.SetValue(target, value, null);
        }
    }

    static void TrySetEnum(object target, string propertyName, string enumValueName)
    {
        var p = target.GetType().GetProperty(propertyName);
        if (p == null || !p.CanWrite || !p.PropertyType.IsEnum) return;
        try
        {
            p.SetValue(target, System.Enum.Parse(p.PropertyType, enumValueName), null);
        }
        catch (System.Exception)
        {
            // 属性名或枚举值在当前 URP 版本里不存在，忽略即可
        }
    }
}
