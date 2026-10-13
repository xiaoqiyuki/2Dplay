using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 玩家近战攻击 + 打击感反馈。
///
/// 【命中判定】用的是"单帧判定"：攻击开始后经过 hitFrameTime，在身前打一个方框查一次，
/// 查到谁身上有 Health 就扣血。不做持续判定，也不给角色加子碰撞体 ——
/// 因为判定帧越少，玩家越容易建立"什么时候能打到"的肌肉记忆，也越不容易出现"一次挥砍打两下"。
///
/// 【打击感三件套】都只在"真的打中了"的时候才播：
///   1. 命中顿帧 —— 把 Time.timeScale 压到很低一小会儿，让命中"有重量"
///   2. 屏幕震动 —— 调 PlatformerCamera.Shake()
///   3. 命中火花 —— 在命中点炸出几块小方块（复用 DustPuff）
///
/// 输入用的是内置的 Player/Attack（默认绑鼠标左键、Enter、手柄 Button West）。
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerCombat : MonoBehaviour
{
    [Header("攻击")]
    public int attackDamage = 2;
    [Tooltip("一次攻击的总时长（秒），这期间不能再攻击")]
    public float attackDuration = 0.22f;
    [Tooltip("攻击开始后多久做命中判定（秒）。调大 = 出招更慢、更\"重\"")]
    public float hitFrameTime = 0.08f;
    [Tooltip("判定框相对玩家中心的水平偏移（像素），朝哪边打就往哪边偏。\n" +
             "原来是 14 —— 从玩家中心只够到 27 像素（约 1.7 格），必须贴脸才打得到")]
    public float hitboxOffsetX = 20f;
    public float hitboxOffsetY = 0f;
    [Tooltip("判定框大小（像素）。原 26×22 太短；现在够到约 38 像素（2.4 格）")]
    public Vector2 hitboxSize = new Vector2(36f, 24f);

    [Header("打击感 · 命中顿帧")]
    [Tooltip("命中瞬间把时间压到多少倍。0.05 = 几乎停住；0.2 = 只是明显变慢，没那么\"硬\"")]
    [Range(0.01f, 1f)] public float hitStopScale = 0.05f;
    [Tooltip("顿帧持续多久（真实时间，秒）。0.03 左右比较舒服；设 0 就是关掉顿帧")]
    public float hitStopDuration = 0.035f;

    [Header("打击感 · 屏幕震动")]
    public float shakeDuration = 0.12f;
    [Tooltip("震幅（像素）")]
    public float shakeMagnitude = 3f;

    [Header("打击感 · 命中火花")]
    public Color sparkColor = new Color(1f, 0.95f, 0.6f, 1f);
    [Tooltip("火花方块的最小 / 最大数量")]
    public int sparkMinCount = 4;
    public int sparkMaxCount = 7;
    [Tooltip("火花初速度范围（像素/秒）")]
    public Vector2 sparkSpeedRange = new Vector2(60f, 150f);
    public float sparkLifetime = 0.22f;

    [Header("弹刀（Parry）")]
    [Tooltip("按下弹刀键后，判定窗口持续多久（秒）。这是弹刀手感最核心的参数，调小 = 更难更硬核")]
    public float parryWindow = 0.15f;
    [Tooltip("按下弹刀键后，多久内不能再按（秒）。没弹到也要付代价，防止无脑连按")]
    public float parryCooldown = 0.45f;
    [Tooltip("弹刀成功后额外获得的免疫时间（秒），避免弹完立刻又被同一个敌人碰到")]
    public float parryInvincibility = 0.35f;

    [Header("弹刀反馈")]
    [Tooltip("弹刀成功的顿帧时长（秒），比普通命中的更长、更有分量")]
    public float parryHitStopDuration = 0.12f;
    public float parryShakeMagnitude = 7f;
    public Color parrySparkColor = new Color(0.7f, 0.95f, 1f, 1f);
    [Tooltip("弹刀火花数量")]
    public int parrySparkCount = 14;

    [Header("弹刀 · 视觉预警")]
    [Tooltip("弹刀窗口开着的时候，玩家身上会套一个光环。\n" +
             "没有它的话，玩家根本不知道窗口还剩多久 —— 弹刀会变成纯运气。")]
    public bool showParryShield = true;
    public Color parryShieldColor = new Color(0.65f, 0.95f, 1f, 0.9f);
    [Tooltip("光环直径（像素）")]
    public float parryShieldSize = 26f;

    [Header("弹刀 · 奖励")]
    [Tooltip("弹刀成功后补几发远程弹药。\n" +
             "这样\"看准时机弹一下 → 立刻回敬一发\"就成了一条连招，\n" +
             "而不是两套互不相干的系统。")]
    public int parryAmmoReward = 1;

    PlayerController _player;
    InputAction _attackAction;
    InputAction _parryAction;
    Sprite _sparkSprite;

    float _attackTimer;
    bool _hitDone;
    float _parryTimer;
    float _parryCooldownTimer;
    float _prevParryValue;      // 上一帧的弹刀键值，用来自己做"按下那一帧"的边沿检测
    SpriteRenderer _shield;

    // ---- 顿帧的全局状态 ----
    // 用 static 是故意的：Time.timeScale 是全工程唯一的，如果每个实例各存一份"恢复值"，
    // 两个实例同时顿帧时，后结束的那个会把时间恢复成"慢动作值"，游戏就永久卡在慢速了。
    // 所以这里改成共享计数器：第一次顿帧时记下原始值，最后一个结束时才恢复。
    static int _activeHitStops;
    static float _savedTimeScale = 1f;

    /// <summary>
    /// 关闭"进入 Play 模式不重载程序集"时，静态字段会跨播放会话残留。
    /// 不重置的话，上一次在顿帧中途停止播放，会让下一次的计数器从非零开始，永远恢复不了。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetHitStopStatics()
    {
        _activeHitStops = 0;
        _savedTimeScale = 1f;
    }

    /// <summary>是否正在挥砍中（外部想"攻击时不能做某事"可以读它）</summary>
    public bool IsAttacking => _attackTimer > 0f;

    /// <summary>弹刀判定窗口是否开着。敌人碰到玩家时会先问这个。</summary>
    public bool IsParryWindowActive => _parryTimer > 0f;

    void Awake()
    {
        _player = GetComponent<PlayerController>();
        _sparkSprite = Greybox.Solid(Color.white, 2, 2);

        InputActionAsset asset = InputSystem.actions;
        if (asset != null)
        {
            _attackAction = asset.FindAction("Player/Attack", false);
            _parryAction = asset.FindAction("Player/Interact", false);
        }

        if (_attackAction == null)
        {
            Debug.LogWarning(
                "[PlayerCombat] 找不到 Player/Attack，攻击功能已禁用（移动跳跃不受影响）。" +
                "如果你在 Actions Editor 里改过名字，把 Awake() 里的 \"Player/Attack\" 一起改掉。", this);
        }
        else if (!_attackAction.enabled)
        {
            _attackAction.Enable();
        }

        if (_parryAction == null)
        {
            Debug.LogWarning(
                "[PlayerCombat] 找不到 Player/Interact，弹刀功能已禁用（其他功能正常）。" +
                "内置的 Interact 绑的是 E 键和手柄 Y 键；想换成鼠标右键，" +
                "到 Project Settings → Input System Package 里给 Interact 加一条 Right Button [Mouse] 绑定。", this);
        }
        else if (!_parryAction.enabled)
        {
            _parryAction.Enable();
        }
    }

    void Update()
    {
        UpdateParry();

        // 先读输入，再推进计时，顺序不能反 —— 否则刚按下的那一帧会被当成"已经过了一帧"
        if (_attackAction != null && _attackTimer <= 0f && _attackAction.WasPressedThisFrame())
        {
            _attackTimer = attackDuration;
            _hitDone = false;
            AudioManager.Play("sfx_swing");
        }

        if (_attackTimer <= 0f) return;

        _attackTimer = Mathf.Max(_attackTimer - Time.deltaTime, 0f);

        // 到达判定帧
        if (!_hitDone && _attackTimer <= attackDuration - hitFrameTime)
        {
            _hitDone = true;
            DoHitCheck();
        }
    }

    // ---------------------------------------------------------------- 弹刀
    void UpdateParry()
    {
        if (_parryCooldownTimer > 0f)
        {
            _parryCooldownTimer = Mathf.Max(_parryCooldownTimer - Time.deltaTime, 0f);
        }
        if (_parryTimer > 0f)
        {
            _parryTimer = Mathf.Max(_parryTimer - Time.deltaTime, 0f);
        }

        UpdateParryShield();

        if (_parryAction == null) return;

        // 【不要用 WasPressedThisFrame()】
        // 内置的 Player/Interact 上挂着 Hold 交互（"按住 0.2 秒才算触发"），
        // 那是给"长按交互"设计的。Hold 交互会接管 action 的 phase，
        // 于是 WasPressedThisFrame() 变成"持续为真"而不是"只在按下那一帧为真" ——
        // 弹刀窗口就会被每帧刷新、等于永远开着，敌人一碰到玩家就被判定成"被弹刀"，
        // 表现就是"追击中碰到一次玩家就停下来"。
        //
        // 所以这里绕开 action 的 phase，直接读控制器的值、自己做"本帧按下"的边沿检测。
        // 这样不管你的 actions 资源上挂了什么交互，弹刀都只在真正按下的那一帧触发一次。
        float parryValue = _parryAction.ReadValue<float>();
        bool pressedThisFrame = parryValue > 0.5f && _prevParryValue <= 0.5f;
        _prevParryValue = parryValue;

        if (!pressedThisFrame) return;
        if (_parryCooldownTimer > 0f) return;

        // 按下就进冷却：没弹到也要付出代价，否则可以无脑连按变成全程无敌
        _parryTimer = parryWindow;
        _parryCooldownTimer = parryCooldown;
    }

    /// <summary>
    /// 敌人撞到玩家时先调这里。如果正处在弹刀窗口内，就算弹刀成功：
    /// 关掉窗口、播反馈、给一小段免疫，并返回 true（敌人据此决定自己被弹开）。
    ///
    /// 注意这里【不】扣血、也【不】走 Health.TakeDamage —— 弹刀的定义就是"这一下不算数"。
    /// </summary>
    public bool TryParry()
    {
        if (_parryTimer <= 0f) return false;

        _parryTimer = 0f;
        _parryCooldownTimer = parryCooldown;

        // 弹刀奖励：补远程弹药 → "弹一下、回敬一发"是一条完整连招
        if (_player != null && parryAmmoReward > 0) _player.RefillRanged(parryAmmoReward);

        PlayParryFeedback();
        return true;
    }

    /// <summary>
    /// 弹刀窗口开着的时候，在玩家身上套一个会呼吸的光环。
    ///
    /// 为什么必须有：窗口只有 0.15 秒，没有任何视觉反馈的话玩家完全不知道
    /// "我刚才到底有没有进入判定" —— 弹刀会退化成"无脑连按碰运气"。
    /// 有了光环，它才是一个可以练的技术动作。
    /// 用代码画圆环，不引任何图片资源（和心形血条同一个思路）。
    /// </summary>
    void UpdateParryShield()
    {
        if (!showParryShield) return;

        if (_shield == null)
        {
            var go = new GameObject("ParryShield");
            go.transform.SetParent(transform, false);
            _shield = go.AddComponent<SpriteRenderer>();
            _shield.sprite = MakeRingSprite(24);
            _shield.sortingOrder = 18;
        }

        bool active = _parryTimer > 0f;
        _shield.enabled = active;
        if (!active) return;

        // 越接近窗口结束越淡，玩家能看出"快没了"
        float t = parryWindow > 0f ? Mathf.Clamp01(_parryTimer / parryWindow) : 0f;
        Color c = parryShieldColor;
        c.a *= Mathf.Lerp(0.35f, 1f, t);
        _shield.color = c;

        float scale = parryShieldSize / 24f;      // 贴图是 24 像素、16 PPU 建的
        _shield.transform.localScale = new Vector3(scale, scale, 1f);
    }

    /// <summary>生成一个圆环贴图（白色，用 color 染色）</summary>
    static Sprite MakeRingSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        float c = (size - 1) * 0.5f;
        float outer = c;
        float inner = c - 2.0f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - c;
                float dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                bool on = d <= outer && d >= inner;
                tex.SetPixel(x, y, on ? Color.white : new Color(0f, 0f, 0f, 0f));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                             PlayerController.PixelsPerUnit);
    }

    void PlayParryFeedback()
    {
        SpawnSparks(transform.position, parrySparkCount, parrySparkColor, 0.32f);

        // 弹刀是全局最"爽"的一下，音效给足
        AudioManager.PlayFlat("sfx_parry", 1f);

        Camera cam = Camera.main;
        if (cam != null)
        {
            PlatformerCamera follow = cam.GetComponent<PlatformerCamera>();
            if (follow != null) follow.Shake(0.18f, parryShakeMagnitude);
        }

        Health health = GetComponent<Health>();
        if (health != null) health.GrantInvincibility(parryInvincibility);

        StartCoroutine(HitStopRoutine(parryHitStopDuration));
    }

    void DoHitCheck()
    {
        const float ppu = PlayerController.PixelsPerUnit;

        int facing = _player != null ? _player.Facing : 1;
        Vector2 center = (Vector2)transform.position
                         + new Vector2(facing * hitboxOffsetX / ppu, hitboxOffsetY / ppu);
        Vector2 size = new Vector2(hitboxSize.x / ppu, hitboxSize.y / ppu);

        Collider2D[] hits = Physics2D.OverlapBoxAll(center, size, 0f);
        Vector2 hitPoint = center;
        bool landed = false;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D h = hits[i];
            if (h == null) continue;
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;

            Health health = h.GetComponentInParent<Health>();
            if (health == null) continue;
            if (health.gameObject == gameObject) continue;   // 别打到自己

            if (health.TakeDamage(attackDamage, transform.position))
            {
                landed = true;
                hitPoint = h.bounds.center;
            }
        }

        if (landed) PlayHitFeedback(hitPoint);
    }

    // ---------------------------------------------------------------- 打击感
    void PlayHitFeedback(Vector2 point)
    {
        SpawnSparks(point);

        AudioManager.Play("sfx_hit");

        Camera cam = Camera.main;
        if (cam != null)
        {
            PlatformerCamera follow = cam.GetComponent<PlatformerCamera>();
            if (follow != null) follow.Shake(shakeDuration, shakeMagnitude);
        }

        if (hitStopDuration > 0f && hitStopScale < 1f)
        {
            StartCoroutine(HitStopRoutine(hitStopDuration));
        }
    }

    /// <summary>
    /// 顿帧：把时间压慢一小会儿。参数是持续多久（真实时间，秒）。
    ///
    /// 两个容易踩的点：
    ///   1. 必须用 WaitForSecondsRealtime，不能用 WaitForSeconds —— 因为 Time.timeScale
    ///      已经被压低了，用缩放后的时间会等上二十倍那么久。
    ///   2. 恢复时不能直接用"自己进协程时记下的值"，必须由共享计数器决定谁来恢复。
    ///      否则两个实例同时顿帧时，后结束的那个会把时间恢复成慢动作值，游戏就永久卡住了。
    /// </summary>
    IEnumerator HitStopRoutine(float duration)
    {
        if (_activeHitStops == 0)
        {
            _savedTimeScale = Time.timeScale;
        }
        _activeHitStops++;

        Time.timeScale = hitStopScale;

        yield return new WaitForSecondsRealtime(duration);

        _activeHitStops--;

        if (_activeHitStops <= 0)
        {
            _activeHitStops = 0;

            // 【别在暂停时把时间恢复回来】
            // Time.timeScale 是全工程唯一的。如果顿帧期间游戏被暂停（比如玩家正好在这
            // 几十毫秒里按了 ESC），这里恢复成 _savedTimeScale 会把暂停直接解除 ——
            // 而且因为暂停界面还在，看起来像"菜单卡住了但游戏在动"。
            if (!GameFlow.IsGamePaused)
            {
                Time.timeScale = _savedTimeScale;
            }
        }
    }

    void SpawnSparks(Vector2 point)
    {
        SpawnSparks(point, Random.Range(sparkMinCount, sparkMaxCount + 1), sparkColor, sparkLifetime);
    }

    void SpawnSparks(Vector2 point, int count, Color color, float lifetime)
    {
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("HitSpark");
            go.transform.SetParent(transform.parent, false);
            go.transform.position = point;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _sparkSprite;
            sr.color = color;
            sr.sortingOrder = 20;    // 画在角色前面

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float speed = Random.Range(sparkSpeedRange.x, sparkSpeedRange.y) / PlayerController.PixelsPerUnit;

            var puff = go.AddComponent<DustPuff>();
            puff.velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
            puff.gravity = -6f;
            puff.lifetime = lifetime;
            puff.fadeStart = 0.2f;
        }
    }
}
