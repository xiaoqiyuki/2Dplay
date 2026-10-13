using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 游戏流程总控：主菜单 / 游戏中 / 暂停 / 死亡 / 通关。
///
/// 为什么要有这个：
///   1. 在它出现之前，玩家血量归零之后【什么都不会发生】—— 会顶着 0 血继续走。
///   2. 打死 Boss 之后也没有任何反馈，玩家不知道"我赢了"。
///   3. 试玩者给差评的第一原因就是"没有菜单和存档"。
///
/// 【为什么用 OnGUI 而不是 UI Canvas】
/// 零依赖。加一个 Canvas + EventSystem + InputSystemUIInputModule 是三个新的出错点，
/// 而这个项目已经因为"环境没配对"卡过很多次了。OnGUI 换来的代价只是样式朴素。
///
/// 【和顿帧的关系，这里有个坑】
/// Time.timeScale 是全工程唯一的。PlayerCombat 的顿帧会先存下当前值、结束后恢复。
/// 如果顿帧期间游戏被暂停（timeScale 归 0），顿帧结束时会把 timeScale 恢复成 1，
/// **暂停就被解除了**。所以 PlayerCombat 恢复前必须先问一句 IsGamePaused。
/// </summary>
public class GameFlow : MonoBehaviour
{
    public enum State { Menu, Help, Playing, Paused, Dead, Victory }

    [Header("启动")]
    [Tooltip("勾上则先显示主菜单再开始。测试时可以关掉，一按 Play 直接进游戏")]
    public bool showMenuOnStart = true;

    [Header("死亡")]
    [Tooltip("死亡慢动作倍率")]
    public float deathSlowMoScale = 0.25f;
    [Tooltip("死亡后多久自动在检查点复活（秒，真实时间）")]
    public float deathRespawnDelay = 1.4f;
    [Tooltip("复活后的无敌时间（秒）。防 Boss 守在检查点旁边把你连死")]
    public float respawnInvincibilityTime = 2.0f;

    [Header("存档 PlayerPrefs 键名")]
    public string bestTimeKey = "2Dplay_BestTime";
    public string bestDeathsKey = "2Dplay_BestDeaths";
    public string clearedKey = "2Dplay_Cleared";

    [Header("难度")]
    [Tooltip("在主菜单里可以改。难度是在点「开始游戏」之后才应用到场景上的 —— " +
             "因为关卡在 Awake 里就建好了，那时候玩家还没选")]
    public Difficulty difficulty = Difficulty.Normal;

    public State Current { get; private set; } = State.Menu;

    /// <summary>游戏是否处于"不该跑"的状态。PlayerCombat 的顿帧恢复会读它。</summary>
    public static bool IsGamePaused
    {
        get { return Instance != null && Instance.Current != State.Playing && Instance.Current != State.Dead; }
    }

    public static GameFlow Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Time.timeScale = 1f;      // 兜底：上一次会话如果在暂停状态退出，别把时间卡在 0
    }

    TestLevelBootstrap _bootstrap;
    Health _playerHealth;
    PlayerController _player;
    BossController _boss;

    float _runTime;
    int _deaths;
    float _deathTimer;
    bool _victoryRecorded;
    readonly List<AchievementManager.Entry> _newAchievements = new List<AchievementManager.Entry>();

    // ---- 操作说明界面 ----
    // 从主菜单进来 → 看完直接开始游戏
    // 从暂停菜单进来 → 看完返回暂停
    // 所以要知道"从哪来的"。
    State _helpReturnState = State.Menu;
    float _helpInputDelay;

    /// <summary>
    /// 重开一局时重新登记场上的敌人。
    ///
    /// 为什么需要：敌人是关卡生成时创建的，重开只是把玩家送回出生点，
    /// 敌人还是原来那批（没有重新生成）。而 ResetRun() 把计数清零了，
    /// 如果不重新登记，"生成总数"会一直是 0 ——
    /// 结果就是"杀光敌人"这个成就永远判不出来。
    /// </summary>
    void ReregisterEnemies()
    {
        EnemyPatrol[] enemies = FindObjectsByType<EnemyPatrol>(FindObjectsSortMode.None);
        for (int i = 0; i < enemies.Length; i++)
        {
            AchievementManager.NotifyEnemySpawned(enemies[i]);
        }

        Debug.Log(string.Format("[GameFlow] 重新登记了 {0} 个敌人（成就统计用）", enemies.Length), this);
    }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        _bootstrap = FindFirstObjectByType<TestLevelBootstrap>();
        _player = FindFirstObjectByType<PlayerController>();

        if (_player != null)
        {
            _playerHealth = _player.GetComponent<Health>();
            if (_playerHealth != null) _playerHealth.Died += OnPlayerDied;
        }
        else
        {
            Debug.LogWarning("[GameFlow] 场景里没找到玩家，死亡流程不会生效。", this);
        }

        _boss = FindFirstObjectByType<BossController>();

        if (showMenuOnStart)
        {
            SetState(State.Menu);
        }
        else
        {
            // 跳过主菜单时也要应用一次难度，否则用的是生成时的默认值
            DifficultySettings.Apply(difficulty);
            if (_playerHealth != null) _playerHealth.GrantInvincibility(respawnInvincibilityTime);
            SetState(State.Playing);
        }
    }

    void OnDisable()
    {
        // 别把编辑器/下一次会话卡在暂停状态
        Time.timeScale = 1f;

        if (_playerHealth != null) _playerHealth.Died -= OnPlayerDied;
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;

        // ---- ESC：暂停 / 继续 ----
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (Current == State.Playing) SetState(State.Paused);
            else if (Current == State.Paused) SetState(State.Playing);
        }

        if (Current == State.Playing)
        {
            _runTime += Time.deltaTime;
            CheckBossDefeated();
            return;
        }

        // ---- 操作说明：按任意键 / 点继续 ----
        if (Current == State.Help)
        {
            // 用 unscaledDeltaTime：说明页时 timeScale 是 0
            _helpInputDelay = Mathf.Max(_helpInputDelay - Time.unscaledDeltaTime, 0f);

            if (_helpInputDelay <= 0f && kb != null && kb.anyKey.wasPressedThisFrame)
            {
                // ESC 不该当成"任意键" —— 它在这个界面里没有别的含义，容易被误按
                if (!kb.escapeKey.wasPressedThisFrame) LeaveHelp();
            }
            return;
        }

        // ---- 死亡：等一会儿自动在检查点复活 ----
        if (Current == State.Dead)
        {
            _deathTimer -= Time.unscaledDeltaTime;
            if (_deathTimer <= 0f) RespawnAfterDeath();
        }
    }

    /// <summary>离开操作说明：从主菜单进来的就直接开始，从暂停进来的就回暂停。</summary>
    void LeaveHelp()
    {
        if (_helpReturnState == State.Menu)
        {
            RestartRun();          // 直接开打
        }
        else
        {
            SetState(_helpReturnState);
        }
    }

    // ---------------------------------------------------------------- 状态
    void SetState(State next)
    {
        Current = next;

        // 暂停 / 菜单时把 BGM 压下去（Time.timeScale 不影响音频，得单独处理）
        AudioManager.SetBgmDucked(next != State.Playing);

        switch (next)
        {
            case State.Playing:
                Time.timeScale = 1f;
                break;
            case State.Dead:
                Time.timeScale = deathSlowMoScale;
                break;
            default:
                Time.timeScale = 0f;      // 菜单 / 暂停 / 通关都冻结世界
                break;
        }
    }

    /// <summary>
    /// 打开操作说明。
    /// from = 从哪个界面进来的，决定看完之后去哪（主菜单 → 直接开打；暂停 → 回暂停）。
    /// </summary>
    void ShowHelp(State from)
    {
        _helpReturnState = from;
        // 刚进来的那一帧不接受"按任意键继续" ——
        // 否则点"开始游戏"的那次按键/点击会立刻把说明页翻过去，玩家什么都看不到。
        _helpInputDelay = 0.35f;
        SetState(State.Help);
    }

    /// <summary>带点击音的按钮。所有菜单按钮都走这里，免得漏掉某个。</summary>
    static bool MenuButton(Rect rect, string label)
    {
        if (GUI.Button(rect, label))
        {
            AudioManager.PlayFlat("sfx_ui");
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 死亡
    void OnPlayerDied()
    {
        if (Current != State.Playing) return;

        _deaths++;
        _deathTimer = deathRespawnDelay;
        SetState(State.Dead);

        AchievementManager.NotifyPlayerDied();

        AudioManager.PlayFlat("sfx_death");
        SpawnDeathBurst();

        Debug.Log(string.Format("[GameFlow] 玩家死亡（第 {0} 次），{1:0.0} 秒后在检查点复活",
                  _deaths, deathRespawnDelay), this);
    }

    void RespawnAfterDeath()
    {
        if (_playerHealth != null) _playerHealth.ResetFull();
        if (_bootstrap != null) _bootstrap.RespawnPlayer();

        // ---- 防"连死" ----
        // 检查点如果离 Boss 太近，玩家复活时 Boss 可能就站在旁边，
        // 一睁眼又被撞死，反复几次就再也起不来了。两道保险：
        //   1. 把 Boss 送回它的出生点，并且清掉激活状态
        //   2. 给玩家一段复活无敌
        if (_boss == null) _boss = FindFirstObjectByType<BossController>();
        if (_boss != null) _boss.ResetToSpawn();

        if (_playerHealth != null) _playerHealth.GrantInvincibility(respawnInvincibilityTime);

        SetState(State.Playing);
    }

    /// <summary>死亡表现：把玩家炸成一堆方块。复用 DustPuff，不新建系统。</summary>
    void SpawnDeathBurst()
    {
        if (_player == null) return;

        Sprite sprite = Greybox.Solid(new Color(1f, 0.45f, 0.5f, 1f), 3, 3);

        for (int i = 0; i < 24; i++)
        {
            var go = new GameObject("DeathDust");
            go.transform.position = _player.transform.position + new Vector3(
                Random.Range(-0.4f, 0.4f), Random.Range(-0.4f, 0.4f), 0f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = new Color(0.36f, 0.78f, 1f, 1f);
            sr.sortingOrder = 20;

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float spd = Random.Range(60f, 200f) / PlayerController.PixelsPerUnit;

            var puff = go.AddComponent<DustPuff>();
            puff.velocity = new Vector2(Mathf.Cos(angle), Mathf.Abs(Mathf.Sin(angle))) * spd;
            puff.gravity = -16f;
            puff.lifetime = 0.8f;
            puff.fadeStart = 0.6f;
        }
    }

    // ---------------------------------------------------------------- 通关
    void CheckBossDefeated()
    {
        if (_victoryRecorded) return;

        // Boss 被击败后 BossController 会把自己 Destroy 掉，所以"曾经有、现在没了"就是通关
        if (_boss == null) return;                    // 这一关根本没有 Boss
        if (_boss != null && _boss.gameObject != null) return;

        _victoryRecorded = true;
        OnVictory();
    }

    /// <summary>Boss 也可以直接调这个，比上面的轮询更及时</summary>
    public void NotifyBossDefeated()
    {
        if (_victoryRecorded) return;
        _victoryRecorded = true;
        OnVictory();
    }

    void OnVictory()
    {
        SetState(State.Victory);

        AudioManager.StopBgm();
        AudioManager.PlayFlat("sfx_victory", 1f);

        // 成就结算
        _newAchievements.Clear();
        List<AchievementManager.Entry> got = AchievementManager.EvaluateOnVictory(difficulty);
        for (int i = 0; i < got.Count; i++) _newAchievements.Add(got[i]);

        float bestTime = PlayerPrefs.GetFloat(bestTimeKey, -1f);
        int bestDeaths = PlayerPrefs.GetInt(bestDeathsKey, int.MaxValue);

        bool newRecord = false;
        // 记录规则：死亡次数少优先；一样少再比时间。这样"稳"比"快"更值钱。
        if (_deaths < bestDeaths || (bestDeaths == int.MaxValue) ||
            (_deaths == bestDeaths && (bestTime < 0f || _runTime < bestTime)))
        {
            newRecord = true;
            PlayerPrefs.SetFloat(bestTimeKey, _runTime);
            PlayerPrefs.SetInt(bestDeathsKey, _deaths);
        }
        PlayerPrefs.SetInt(clearedKey, 1);
        PlayerPrefs.Save();

        Debug.Log(string.Format("[GameFlow] ===== 通关！用时 {0:0.0} 秒，死亡 {1} 次{2} =====",
                  _runTime, _deaths, newRecord ? "（新纪录）" : ""), this);
    }

    void RestartRun()
    {
        _runTime = 0f;
        _deaths = 0;
        _victoryRecorded = false;
        _newAchievements.Clear();
        _boss = FindFirstObjectByType<BossController>();

        // 清空本局统计。必须在难度应用和敌人重新登记之前 ——
        // 否则第二次开局时敌人不会重新登记（它们还是上一局那批），
        // 统计数会一直是 0，"杀光"永远判不出来。
        AchievementManager.ResetRun();
        ReregisterEnemies();

        // 难度在这里应用：场景早就建好了，现在回头把所有敌人重新配一遍
        DifficultySettings.Apply(difficulty);

        if (_playerHealth != null) _playerHealth.ResetFull();
        if (_boss != null) _boss.ResetToSpawn();

        if (_bootstrap != null)
        {
            // 清掉上一局的检查点，否则"重新开始"会从上次死的地方复活
            Checkpoint.ClearCurrent();
            _bootstrap.RespawnPlayer();
        }

        if (_playerHealth != null) _playerHealth.GrantInvincibility(respawnInvincibilityTime);

        SetState(State.Playing);
    }

    // ---------------------------------------------------------------- 界面
    void OnGUI()
    {
        switch (Current)
        {
            case State.Menu: DrawMenu(); break;
            case State.Paused: DrawPaused(); break;
            case State.Help: DrawHelp(); break;
            case State.Dead: DrawDead(); break;
            case State.Victory: DrawVictory(); break;
        }
    }

    void DrawMenu()
    {
        FillScreen(new Color(0.05f, 0.06f, 0.09f, 0.92f));
        DrawTitle("2D 横版闯关", 0.13f, 44);

        // ---- 难度选择 ----
        DrawCentered("难度", Screen.height * 0.27f, 15);

        float bw = 110f, bh = 38f, bgap = 10f;
        float totalW = bw * 3f + bgap * 2f;
        float bx = (Screen.width - totalW) * 0.5f;
        float by = Screen.height * 0.32f;

        DrawDifficultyButton(Difficulty.Easy, new Rect(bx, by, bw, bh));
        DrawDifficultyButton(Difficulty.Normal, new Rect(bx + bw + bgap, by, bw, bh));
        DrawDifficultyButton(Difficulty.Hard, new Rect(bx + (bw + bgap) * 2f, by, bw, bh));

        // 当前档位说明
        DifficultySettings.Profile prof = DifficultySettings.Get(difficulty);
        DrawCentered(prof.Description, by + bh + 10f, 13);

        // ---- 开始 ----
        float w = 260f, h = 46f;
        float x = (Screen.width - w) * 0.5f;
        float y = Screen.height * 0.50f;

        if (MenuButton(new Rect(x, y, w, h), "开始游戏")) ShowHelp(State.Menu);

        bool cleared = PlayerPrefs.GetInt(clearedKey, 0) == 1;
        if (cleared)
        {
            float t = PlayerPrefs.GetFloat(bestTimeKey, 0f);
            int d = PlayerPrefs.GetInt(bestDeathsKey, 0);
            DrawCentered(string.Format("最好成绩：{0:0.0} 秒 · 死亡 {1} 次", t, d), y + h + 18f, 14);
        }
        else
        {
            DrawCentered("还没有通关过", y + h + 18f, 14);
        }

        DrawCentered("WASD 移动 · 空格跳/二段跳 · Shift 冲刺 · 左键近战 · C 远程 · E 弹刀 · ESC 暂停",
                     Screen.height - 34f, 13);

        DrawAchievementList(18f, Screen.height - 172f);
    }

    /// <summary>难度按钮。选中的那个用实心色，其余用暗色。</summary>
    void DrawDifficultyButton(Difficulty d, Rect rect)
    {
        bool selected = difficulty == d;
        DifficultySettings.Profile prof = DifficultySettings.Get(d);

        Color old = GUI.color;
        GUI.color = selected ? new Color(0.35f, 0.75f, 1f, 1f) : new Color(0.28f, 0.30f, 0.38f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 17,
            alignment = TextAnchor.MiddleCenter,
            fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
        };
        style.normal.textColor = selected ? Color.black : new Color(0.8f, 0.84f, 0.9f, 1f);

        if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            if (difficulty != d) AudioManager.PlayFlat("sfx_ui");
            difficulty = d;
        }
        GUI.Label(rect, prof.Name, style);
    }

    void DrawPaused()
    {
        FillScreen(new Color(0f, 0f, 0f, 0.62f));
        DrawTitle("已暂停", 0.30f, 36);

        float w = 240f, h = 42f;
        float x = (Screen.width - w) * 0.5f;
        float y = Screen.height * 0.44f;

        if (MenuButton(new Rect(x, y, w, h), "继续")) SetState(State.Playing);
        if (MenuButton(new Rect(x, y + h + 10f, w, h), "操作说明")) ShowHelp(State.Paused);
        if (MenuButton(new Rect(x, y + (h + 10f) * 2f, w, h), "重新开始")) RestartRun();
        if (MenuButton(new Rect(x, y + (h + 10f) * 3f, w, h), "回主菜单")) SetState(State.Menu);

        DrawCentered(string.Format("本局：{0:0.0} 秒 · 死亡 {1} 次", _runTime, _deaths),
                     y + (h + 10f) * 4f + 6f, 14);
    }

    // ---------------------------------------------------------------- 操作说明
    // 按键名保持英文（键帽上印的就是英文），说明用中文。
    // 一屏放得下是刻意的 —— 分成两页的话玩家不会翻第二页。
    static readonly string[,] ControlRows =
    {
        { "W  A  S  D",   "左右移动" },
        { "空格",          "跳跃 · 长按跳更高 · 空中再按一次是二段跳" },
        { "左 Shift",     "冲刺 · 途中无敌，可以直接穿过敌人" },
        { "鼠标左键 / Enter", "近战挥砍" },
        { "C",            "远程弹道 · 有弹药上限，打空了会慢慢回复" },
        { "E",            "弹刀 · 在被打到前的一瞬间按下，把攻击弹开" },
        { "R",            "回到最近的检查点" },
        { "ESC",          "暂停 / 继续" },
    };

    void DrawHelp()
    {
        FillScreen(new Color(0.05f, 0.06f, 0.09f, 0.95f));
        DrawTitle("操作说明", 0.07f, 36);

        float s = Mathf.Max(1f, Screen.height / 360f);
        float rowH = 26f * s;
        float keyW = 150f * s;
        float top = Screen.height * 0.18f;
        float left = Screen.width * 0.5f - keyW - 20f * s;

        var keyStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(15 * s),
            alignment = TextAnchor.MiddleRight,
            fontStyle = FontStyle.Bold,
        };
        keyStyle.normal.textColor = new Color(0.6f, 0.9f, 1f, 1f);

        var descStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(14 * s),
            alignment = TextAnchor.MiddleLeft,
        };
        descStyle.normal.textColor = new Color(0.82f, 0.86f, 0.92f, 1f);

        for (int i = 0; i < ControlRows.GetLength(0); i++)
        {
            float y = top + i * rowH;
            GUI.Label(new Rect(left, y, keyW, rowH), ControlRows[i, 0], keyStyle);
            GUI.Label(new Rect(left + keyW + 16f * s, y, Screen.width - left - keyW - 40f * s, rowH),
                      ControlRows[i, 1], descStyle);
        }

        // ---- 两条最关键的提示 ----
        float tipsY = top + ControlRows.GetLength(0) * rowH + 10f * s;
        DrawHighlighted("冲锋兵变红 = 它要冲了 —— 那是按 E 的最佳时机", tipsY);
        DrawHighlighted("打 Boss 别硬贴：用 C 远程磨它，它跳砸落地时会有冲击波", tipsY + 22f * s);

        // ---- 继续 ----
        float bw = 260f, bh = 44f;
        float bx = (Screen.width - bw) * 0.5f;
        float by = Screen.height - bh - 56f;

        string label = _helpReturnState == State.Menu ? "开始游戏" : "返回";
        if (MenuButton(new Rect(bx, by, bw, bh), label)) LeaveHelp();

        DrawCentered("按任意键继续", by + bh + 6f, 13);
    }

    void DrawDead()
    {
        FillScreen(new Color(0.18f, 0.02f, 0.04f, Mathf.Clamp01(1f - _deathTimer / deathRespawnDelay) * 0.7f));
        DrawTitle("你死了", 0.36f, 40);
        DrawCentered(string.Format("第 {0} 次 · 正在检查点复活…", _deaths), Screen.height * 0.46f, 16);
    }

    void DrawVictory()
    {
        FillScreen(new Color(0.04f, 0.09f, 0.07f, 0.9f));
        DrawTitle("通 关", 0.20f, 48);

        DrawCentered(string.Format("用时 {0:0.0} 秒　·　死亡 {1} 次", _runTime, _deaths),
                     Screen.height * 0.36f, 20);

        float w = 260f, h = 44f;
        float x = (Screen.width - w) * 0.5f;
        float y = Screen.height * 0.48f;

        if (MenuButton(new Rect(x, y, w, h), "再来一次")) RestartRun();
        if (MenuButton(new Rect(x, y + h + 12f, w, h), "回主菜单")) SetState(State.Menu);

        float bt = PlayerPrefs.GetFloat(bestTimeKey, 0f);
        int bd = PlayerPrefs.GetInt(bestDeathsKey, 0);
        DrawCentered(string.Format("最好成绩：{0:0.0} 秒 · 死亡 {1} 次", bt, bd),
                     y + (h + 12f) * 2f + 6f, 14);

        // ---- 本次新解锁的成就 ----
        int unlocked, total;
        AchievementManager.GetProgress(out unlocked, out total);
        DrawCentered(string.Format("成就 {0} / {1}", unlocked, total),
                     y + (h + 12f) * 2f + 30f, 13);

        float listY = y + (h + 12f) * 2f + 52f;

        if (_newAchievements.Count > 0)
        {
            DrawCentered("★ 新解锁", listY, 15);
            listY += 22f;
            for (int i = 0; i < _newAchievements.Count; i++)
            {
                DrawHighlighted(string.Format("★ {0} —— {1}",
                    _newAchievements[i].Title, _newAchievements[i].Desc), listY);
                listY += 20f;
            }
        }
        else
        {
            DrawCentered("本次没有新成就", listY, 13);
        }
    }

    // ---------------------------------------------------------------- 绘制小工具
    static void FillScreen(Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    static void DrawTitle(string text, float yRatio, int fontSize)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
        };
        style.normal.textColor = Color.white;

        var rect = new Rect(0f, Screen.height * yRatio, Screen.width, fontSize * 1.6f);
        GUI.Label(rect, text, style);
    }

    static void DrawCentered(string text, float y, int fontSize)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleCenter,
        };
        style.normal.textColor = new Color(0.78f, 0.82f, 0.9f, 1f);

        GUI.Label(new Rect(0f, y, Screen.width, fontSize * 1.8f), text, style);
    }

    /// <summary>高亮的居中文字（新解锁的成就用）</summary>
    static void DrawHighlighted(string text, float y)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
        };
        style.normal.textColor = new Color(1f, 0.86f, 0.35f, 1f);

        GUI.Label(new Rect(0f, y, Screen.width, 26f), text, style);
    }

    /// <summary>左下角的成就清单：拿到的点亮，没拿到的暗着（给玩家一个"还差什么"）</summary>
    static void DrawAchievementList(float x, float y)
    {
        int unlocked, total;
        AchievementManager.GetProgress(out unlocked, out total);

        var head = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        head.normal.textColor = new Color(0.85f, 0.88f, 0.95f, 1f);
        GUI.Label(new Rect(x, y, 420f, 22f),
                  string.Format("成就  {0} / {1}", unlocked, total), head);

        float ly = y + 24f;
        for (int i = 0; i < AchievementManager.All.Length; i++)
        {
            AchievementManager.Entry e = AchievementManager.All[i];
            bool got = AchievementManager.IsUnlocked(e.Id);

            var s = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            s.normal.textColor = got
                ? new Color(1f, 0.86f, 0.35f, 1f)
                : new Color(0.45f, 0.48f, 0.56f, 1f);

            GUI.Label(new Rect(x, ly, 460f, 20f),
                      (got ? "★ " : "· ") + e.Title + "  —  " + e.Desc, s);
            ly += 19f;
        }
    }
}
