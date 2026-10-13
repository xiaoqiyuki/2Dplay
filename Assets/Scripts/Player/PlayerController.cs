using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 2D 横版闯关 · 主角控制器（Unity / W1 灰盒原型）
///
/// 四种手感优化全部包含：
///   1. 土狼时间 Coyote Time  —— 离开平台后仍有一小段时间可以起跳
///   2. 跳跃缓冲 Jump Buffer  —— 落地前提前按跳，会在落地瞬间自动执行
///   3. 可变跳跃高度 Jump Cut —— 松开跳跃键立刻减速上升（点按小跳 / 长按高跳）
///   4. 非对称重力            —— 下落比上升重，跳跃手感更"脆"
///
/// 另含二段跳与冲刺。
///
/// 【关于单位】Unity 的 1 个世界单位 = 16 像素（因为项目的 Pixels Per Unit = 16）。
/// 本脚本所有可调数值都用「像素」为单位，内部自动除以 16 换算成世界单位，
/// 所以这里的数字可以直接和制作方案文档里的参数表对照，不用心算。
///
/// 【输入】用的是新版 Input System 的 Project-wide Actions（就是
/// Project Settings → Input System Package 里那个 InputSystem_Actions 资源）。
/// 脚本通过 InputSystem.actions 直接访问，所以：
///   · 不需要在 Inspector 里拖任何资源进来
///   · 不需要新建任何 Action
/// 用到的三个 Action 都是 Unity 内置的，名字别改：
///   Player/Move    (Value, Vector2)  左右移动，只取 x 分量
///   Player/Jump    (Button)          跳跃
///   Player/Sprint  (Button)          拿来做冲刺（原名意思是"疾跑"）
///
/// 【需要的组件】脚本会自动补上缺失的：
///   Rigidbody2D  —— Dynamic / Gravity Scale = 0 / Freeze Rotation Z / Continuous / Interpolate
///   BoxCollider2D
///   SpriteRenderer（灰盒阶段可以为空）
///
/// 【兼容性】Unity 6 把 Rigidbody2D.velocity 改名成了 linearVelocity。
/// 如果你用的是 Unity 2022 或更早，请把本文件里所有 linearVelocity 替换回 velocity。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))]
public class PlayerController : MonoBehaviour
{
    /// <summary>1 个世界单位对应多少像素。必须和 Sprite 的 Pixels Per Unit 一致。</summary>
    public const float PixelsPerUnit = 16f;

    public enum State { Idle, Run, Jump, Fall, Dash, Hurt }

    [Header("水平移动（像素/秒、像素/秒²）")]
    [Tooltip("最大跑速")]
    public float maxSpeed = 140f;
    [Tooltip("地面加速度，越大起步越弹")]
    public float groundAccel = 1200f;
    [Tooltip("地面减速度，越大刹车越快")]
    public float groundDecel = 1800f;
    [Tooltip("空中加速度，通常略低于地面，保留一点惯性")]
    public float airAccel = 900f;
    [Tooltip("空中减速度，调小则空中更容易保持惯性")]
    public float airDecel = 600f;

    [Header("跳跃（像素/秒、像素/秒²）")]
    [Tooltip("起跳初速度。跳跃高度与它的平方成正比")]
    public float jumpVelocity = 330f;
    [Tooltip("上升阶段重力")]
    public float gravityUp = 1100f;
    [Tooltip("下落阶段重力，建议 ≥ gravityUp 的 1.3 倍")]
    public float gravityDown = 1600f;
    [Tooltip("最大下落速度，防止落太快穿模")]
    public float maxFallSpeed = 420f;
    [Tooltip("土狼时间：离开平台后还能起跳的宽限时长")]
    [Range(0f, 0.5f)] public float coyoteTime = 0.10f;
    [Tooltip("跳跃缓冲：落地前多久按跳仍然有效")]
    [Range(0f, 0.5f)] public float jumpBufferTime = 0.10f;
    [Tooltip("松手时的上升速度保留比例，越小跳得越矮（0 = 立刻停住）")]
    [Range(0f, 1f)] public float jumpCutMultiplier = 0.45f;
    public bool enableDoubleJump = true;
    public float doubleJumpVelocity = 280f;

    [Header("冲刺（像素/秒）")]
    public bool enableDash = true;
    public float dashSpeed = 600f;
    public float dashTime = 0.15f;
    public float dashCooldown = 0.40f;

    [Header("远程攻击（像素/秒）")]
    [Tooltip("总开关")]
    public bool enableRanged = true;
    [Tooltip("两次射击的最小间隔（秒）")]
    public float rangedCooldown = 0.28f;
    [Tooltip("弹药上限。这只是【默认值】—— 实际值由难度决定，见 DifficultySettings。\n" +
             "有上限才不会变成\"站在远处点掉一切\"")]
    public int rangedMaxAmmo = 4;
    [Tooltip("打空之后每发回多少秒")]
    public float rangedRegenTime = 2.0f;
    [Tooltip("弹道飞行速度（像素/秒）")]
    public float rangedSpeed = 330f;
    [Tooltip("弹道伤害")]
    public int rangedDamage = 2;
    [Tooltip("弹道判定框大小（像素）")]
    public float rangedBoltWidth = 10f;
    public float rangedBoltHeight = 6f;
    [Tooltip("弹道颜色")]
    public Color rangedBoltColor = new Color(0.55f, 0.95f, 1f, 1f);

    [Header("地面检测")]
    [Tooltip("哪些层算地面。默认全部，脚本会自动忽略玩家自己身上的碰撞体")]
    public LayerMask groundMask = ~0;

    [Tooltip("检测框宽度 = 身体宽度 × 这个比例。\n" +
             "调大 = 更容易判定为站在地上，但贴着墙时更容易把【墙】误判成地面，人就粘在墙上滑不下来\n" +
             "调小 = 更不容易粘墙，但站在平台边缘时更容易被判定成踩空\n" +
             "0.7 是两者之间的折中")]
    public float groundCheckWidthRatio = 0.7f;

    [Tooltip("检测框高度（世界单位）。整个框压在脚底以下，不向上探进身体 —— " +
             "向上探就一定会碰到旁边的墙")]
    public float groundCheckHeight = 0.06f;

    [Header("受击")]
    [Tooltip("被打中时水平击退速度（像素/秒）")]
    public float knockbackSpeedX = 220f;
    [Tooltip("被打中时向上弹起的速度（像素/秒）")]
    public float knockbackSpeedY = 150f;
    [Tooltip("受击后失去控制的时间（秒）")]
    public float hurtTime = 0.25f;
    [Tooltip("受击期间水平速度的衰减（像素/秒²）。调小则击退飞得更远")]
    public float hurtDecel = 400f;
    [Tooltip("无敌帧期间精灵闪烁的颜色")]
    public Color damageFlashColor = new Color(1f, 0.4f, 0.4f, 1f);

    [Header("输入")]
    [Tooltip("留空即可 —— 会自动使用 Project Settings 里指定的 Project-wide Actions。" +
             "只有当你想让这个角色改用另一套 Input Action Asset 时，才需要拖东西进来。")]
    [SerializeField] private InputActionAsset inputActionsOverride;

    // ---------------------------------------------------------------- 对外只读状态
    public State CurrentState { get; private set; } = State.Idle;
    public bool IsGrounded { get; private set; }
    /// <summary>当前朝向：1 = 右，-1 = 左。攻击判定要靠它决定往哪边打。</summary>
    public int Facing => _facing;
    /// <summary>是否正在受击硬直中（这期间不接受任何输入）</summary>
    public bool IsHurt => _hurtTimer > 0f;
    public Vector2 Velocity => _rb != null ? _rb.linearVelocity : Vector2.zero;
    public float CoyoteTimer => _coyoteTimer;
    public float JumpBufferTimer => _jumpBufferTimer;
    public int AirJumpsLeft => _airJumpsLeft;
    public float DashCooldownTimer => _dashCooldownTimer;

    /// <summary>最近一次落地的下落速度（像素/秒）。落地之后才有效，调试面板会显示它。</summary>
    public float LastLandingSpeed { get; private set; }

    /// <summary>落地时触发，参数是落地瞬间的下落速度（世界单位/秒，取正值）。</summary>
    public event System.Action<float> Landed;

    Rigidbody2D _rb;
    BoxCollider2D _box;
    SpriteRenderer _sprite;

    InputAction _moveAction;
    InputAction _jumpAction;
    InputAction _dashAction;
    InputAction _rangedAction;
    float _prevRangedValue;
    int _rangedAmmo;
    float _rangedCooldownTimer;
    float _regenTimer;
    Sprite _boltSprite;

    bool _inputReady;
    float _moveInput;
    int _facing = 1;
    float _coyoteTimer;
    float _jumpBufferTimer;
    int _airJumpsLeft;
    float _dashTimer;
    float _dashCooldownTimer;
    int _dashDirection = 1;
    bool _grounded;
    /// <summary>本次腾空期间记录到的最大下落速度（世界单位/秒，正值）</summary>
    float _peakFallSpeed;
    float _hurtTimer;
    Health _health;
    Color _baseColor = Color.white;

    // 冲刺穿过敌人：记录被临时忽略的敌人碰撞体，冲刺结束时逐个还原
    readonly List<Collider2D> _dashIgnoredColliders = new List<Collider2D>();
    bool _dashPassThroughActive;
    float _passThroughStuckTimer;

    /// <summary>
    /// 懒加载 Health。不能在 Awake 里取 —— 组件是运行时 AddComponent 加上的，
    /// 加的顺序变一下就取不到了。用属性包一层，谁来取都不怕。
    /// </summary>
    Health HealthComponent
    {
        get
        {
            if (_health == null) _health = GetComponent<Health>();
            return _health;
        }
    }

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _box = GetComponent<BoxCollider2D>();
        _sprite = GetComponentInChildren<SpriteRenderer>();
        if (_sprite != null) _baseColor = _sprite.color;

        // 物理参数由脚本自己接管，Rigidbody 只负责移动和碰撞
        _rb.bodyType = RigidbodyType2D.Dynamic;
        _rb.gravityScale = 0f;
        _rb.freezeRotation = true;
        _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        // ---- 零摩擦材质：解决"贴着墙会粘在墙上滑不下来" ----
        // 重力是脚本每物理帧手动加的，一帧只给 v.y 增加约 2 单位/秒
        //（gravityDown 1600 ÷ 16 × 0.02）。而玩家顶着墙走时，碰撞解算会产生一个
        // 法向冲量把水平速度顶回来，摩擦力 = μ × 那个法向冲量，默认 μ = 0.4 ——
        // 算出来的摩擦冲量远大于重力一帧给的量，垂直速度就被完全抹平了。
        //
        // 玩家的水平移动本来就是 ApplyHorizontal 脚本算的、地面检测用的是 OverlapBox，
        // 两处都不依赖摩擦，所以零摩擦没有副作用。
        // 唯一的代价：以后站在斜坡上会往下滑（目前关卡全是平台，不受影响）。
        if (_box != null)
        {
            var frictionless = new PhysicsMaterial2D("PlayerFrictionless")
            {
                friction = 0f,
                bounciness = 0f
            };
            _box.sharedMaterial = frictionless;
        }

        _airJumpsLeft = enableDoubleJump ? 1 : 0;
        _rangedAmmo = rangedMaxAmmo;
        _boltSprite = Greybox.Solid(rangedBoltColor, (int)rangedBoltWidth, (int)rangedBoltHeight);

        SetupInput();
    }

    /// <summary>远程弹药（给 HUD 读）</summary>
    public int RangedAmmo { get { return _rangedAmmo; } }
    public int RangedMaxAmmo { get { return rangedMaxAmmo; } }

    /// <summary>补弹药。弹刀成功时会调 —— 让"弹刀→远程"形成一条连招，而不是两套割裂的系统。</summary>
    public void RefillRanged(int amount)
    {
        if (amount <= 0) return;
        _rangedAmmo = Mathf.Min(_rangedAmmo + amount, rangedMaxAmmo);
    }

    /// <summary>
    /// 按难度设置弹药上限，并把弹夹补满。
    /// 难度切换（在主菜单选）和开新一局时都会调到这里。
    ///
    /// 设计上越简单给的弹越多（简单 6 / 一般 4 / 困难 3），
    /// 和其他难度缩放的方向一致 —— 简单档本来就是"让玩家过得去"。
    /// </summary>
    public void ApplyDifficultyAmmo(int maxAmmo)
    {
        rangedMaxAmmo = Mathf.Max(1, maxAmmo);
        _rangedAmmo = rangedMaxAmmo;
        _regenTimer = 0f;
    }

    /// <summary>弹道冷却 / 回弹。用 deltaTime，所以暂停时（timeScale=0）不会走。</summary>
    void TickRanged(float dt)
    {
        if (_rangedCooldownTimer > 0f)
        {
            _rangedCooldownTimer = Mathf.Max(_rangedCooldownTimer - dt, 0f);
        }

        if (_rangedAmmo >= rangedMaxAmmo)
        {
            _regenTimer = 0f;
            return;
        }

        _regenTimer -= dt;
        if (_regenTimer <= 0f)
        {
            _rangedAmmo++;
            _regenTimer = rangedRegenTime;
        }
    }

    // 输入在 Update 里读，物理在 FixedUpdate 里算。
    // 因为有跳跃缓冲计时器兜着，Update 读到 FixedUpdate 消费之间不会丢输入。
    void Update()
    {
        if (!_inputReady) return;
        ReadInput();
        UpdateDamageFlash();
        TickRanged(Time.deltaTime);
    }

    /// <summary>
    /// 受伤无敌帧期间让精灵闪烁。
    /// 注意只认 HasDamageInvincibility（受伤计时），不认 IsInvincible ——
    /// 否则每次冲刺和弹刀都会让角色闪红，看起来很乱。
    /// </summary>
    void UpdateDamageFlash()
    {
        if (_sprite == null) return;

        Health health = HealthComponent;
        bool invincible = health != null && health.HasDamageInvincibility;

        if (invincible)
        {
            float pulse = Mathf.PingPong(Time.time * 12f, 1f);
            _sprite.color = Color.Lerp(_baseColor, damageFlashColor, pulse);
        }
        else if (_sprite.color != _baseColor)
        {
            _sprite.color = _baseColor;
        }
    }

    void FixedUpdate()
    {
        if (!_inputReady) return;

        float dt = Time.fixedDeltaTime;

        TickTimers(dt);

        if (_dashTimer > 0f)
        {
            ApplyDash(dt);
        }
        else
        {
            if (_hurtTimer > 0f)
            {
                // 受击期间不响应左右输入，只做轻微减速，让击退飞得出来。
                // 如果这里调用 ApplyHorizontal，地面减速度（1800）会在 0.1 秒内把击退抹平，
                // 玩家根本感觉不到被撞飞。
                Vector2 hv = _rb.linearVelocity;
                hv.x = Mathf.MoveTowards(hv.x, 0f, hurtDecel / PixelsPerUnit * dt);
                _rb.linearVelocity = hv;
            }
            else
            {
                ApplyHorizontal(dt);
            }

            ApplyGravity(dt);
            ApplyJump();
        }

        // ---- 冲刺的无敌帧 + 穿过敌人 ----
        // 放在这里（速度都设完之后），因为它只改状态、不改这一帧的运动。
        UpdateDashInvincibility();

        // ---- 记录下落速度 ----
        // 关键：必须在这里记，不能等落地那一帧再读 velocity。
        // Unity 的执行顺序是 FixedUpdate → 物理步进 → 碰撞回调，物理模拟是在我们之后跑的，
        // 所以撞击地面的瞬间速度就已经被物理引擎清零了，那时读到的是 0。
        // 后果：报出去的"落地速度"永远只剩重力一帧的累积量（约 32 px/s），
        // 落地尘土这类按速度判断的反馈会被门槛全部挡掉。
        // 正确做法：腾空期间持续记录峰值，落地时把峰值报出去。
        float downwardSpeed = Mathf.Max(-_rb.linearVelocity.y, 0f);
        if (!_grounded && downwardSpeed > _peakFallSpeed)
        {
            _peakFallSpeed = downwardSpeed;
        }

        bool wasGrounded = _grounded;
        _grounded = CheckGrounded();
        IsGrounded = _grounded;

        if (_grounded)
        {
            _coyoteTimer = coyoteTime;
            _airJumpsLeft = enableDoubleJump ? 1 : 0;

            if (!wasGrounded)
            {
                LastLandingSpeed = _peakFallSpeed * PixelsPerUnit;

                // 轻轻落下就别响了 —— 一路小跳全是"咚"会很吵
                if (LastLandingSpeed > 60f) AudioManager.Play("sfx_land", 0.8f);

                if (Landed != null)
                {
                    Landed(_peakFallSpeed);
                }
                _peakFallSpeed = 0f;
            }
        }

        UpdateState();
    }

    // ---------------------------------------------------------------- 输入（新版 Input System）
    void SetupInput()
    {
        InputActionAsset asset = inputActionsOverride != null ? inputActionsOverride : InputSystem.actions;

        if (asset == null)
        {
            Debug.LogError(
                "[PlayerController] 拿不到输入资源。请到 Edit → Project Settings → Input System Package，" +
                "确认 Project-wide Actions 里已经指定了 InputSystem_Actions（新建工程默认会有）。", this);
            return;
        }

        _moveAction = asset.FindAction("Player/Move", false);
        _jumpAction = asset.FindAction("Player/Jump", false);
        _dashAction = asset.FindAction("Player/Sprint", false);
        _rangedAction = asset.FindAction("Player/Crouch", false);

        if (_moveAction == null || _jumpAction == null)
        {
            string missing = "Player/Move 和 Player/Jump";
            if (_moveAction != null) missing = "Player/Jump";
            else if (_jumpAction != null) missing = "Player/Move";

            Debug.LogError(
                "[PlayerController] 在输入资源里找不到 " + missing + "。" +
                "请确认 Action Map 名字是 Player，且里面的 Action 名字没被改过。", this);
            return;
        }

        if (_dashAction == null)
        {
            Debug.LogWarning(
                "[PlayerController] 没找到 Player/Sprint，冲刺功能已禁用（其他功能正常）。" +
                "如果你在 Actions Editor 里改了名字，把 SetupInput() 里的 \"Player/Sprint\" 一起改掉。", this);
        }

        if (_rangedAction == null)
        {
            Debug.LogWarning(
                "[PlayerController] 没找到 Player/Crouch，远程攻击已禁用（其他功能正常）。" +
                "内置的 Crouch 绑的是 C 键和手柄 B 键。想换成鼠标右键，" +
                "到 Project Settings → Input System Package 里给 Crouch 加一条 Right Button [Mouse] 绑定。", this);
        }

        // Project-wide Actions 默认就是启用的，这里只是兜底
        if (!_moveAction.enabled) _moveAction.Enable();
        if (!_jumpAction.enabled) _jumpAction.Enable();
        if (_dashAction != null && !_dashAction.enabled) _dashAction.Enable();
        if (_rangedAction != null && !_rangedAction.enabled) _rangedAction.Enable();

        _inputReady = true;
    }

    void ReadInput()
    {
        // 受击硬直期间完全不接受输入。这里是"全屏蔽"而不是只屏蔽左右 ——
        // 被打飞的时候还能跳起来，会让击退看起来毫无分量。
        if (_hurtTimer > 0f)
        {
            _moveInput = 0f;
            return;
        }

        // 新版 Input System：Move 是个 Vector2，我们只用它的水平分量
        Vector2 move = _moveAction.ReadValue<Vector2>();
        _moveInput = move.x;

        if (Mathf.Abs(_moveInput) > 0.01f)
        {
            _facing = _moveInput > 0f ? 1 : -1;
            if (_sprite != null) _sprite.flipX = _facing < 0;
        }

        if (_jumpAction.WasPressedThisFrame())
        {
            _jumpBufferTimer = jumpBufferTime;
        }

        // 可变跳跃高度：上升途中松手就砍掉一部分速度
        if (_jumpAction.WasReleasedThisFrame())
        {
            Vector2 v = _rb.linearVelocity;
            if (v.y > 0f)
            {
                _rb.linearVelocity = new Vector2(v.x, v.y * jumpCutMultiplier);
            }
        }

        if (_dashAction != null && _dashAction.WasPressedThisFrame())
        {
            TryDash();
        }

        // ---- 远程攻击 ----
        // 【不能用 WasPressedThisFrame】内置的 Player/Crouch 上挂着 Hold 交互
        //（和 Player/Interact 一样），那会接管 action 的 phase，
        // 使 WasPressedThisFrame 变成"持续为真"而不是"只在按下那一帧为真" ——
        // 弹刀那边已经因为这个出过一次 bug，这里直接绕开，自己读值做边沿检测。
        if (_rangedAction != null)
        {
            float v = _rangedAction.ReadValue<float>();
            bool pressed = v > 0.5f && _prevRangedValue <= 0.5f;
            _prevRangedValue = v;

            if (pressed) TryRangedAttack();
        }
    }

    // ---------------------------------------------------------------- 远程攻击
    void TryRangedAttack()
    {
        if (!enableRanged || _rangedCooldownTimer > 0f) return;
        if (_rangedAmmo <= 0) return;
        if (!_inputReady) return;

        _rangedAmmo--;
        _rangedCooldownTimer = rangedCooldown;
        _regenTimer = rangedRegenTime;

        const float ppu = PixelsPerUnit;

        var go = new GameObject("Bolt");
        if (transform.parent != null) go.transform.SetParent(transform.parent, false);

        // 从身体中段偏上一点射出去，和胸口宝石的位置对齐
        go.transform.position = (Vector2)transform.position + new Vector2(_facing * 0.6f, 0.1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _boltSprite;
        sr.sortingOrder = 15;

        var rb = go.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Kinematic;
        rb.gravityScale = 0f;

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(rangedBoltWidth / ppu, rangedBoltHeight / ppu);
        col.isTrigger = true;

        var bolt = go.AddComponent<PlayerProjectile>();
        bolt.direction = _facing;
        bolt.damage = rangedDamage;
        bolt.speed = rangedSpeed;

        AudioManager.Play("sfx_swing", 0.55f, 0.14f);
    }

    // ---------------------------------------------------------------- 水平移动
    void ApplyHorizontal(float dt)
    {
        Vector2 v = _rb.linearVelocity;
        float target = _moveInput * (maxSpeed / PixelsPerUnit);
        float accel = Mathf.Abs(_moveInput) > 0.01f
            ? (_grounded ? groundAccel : airAccel)
            : (_grounded ? groundDecel : airDecel);
        v.x = Mathf.MoveTowards(v.x, target, accel / PixelsPerUnit * dt);
        _rb.linearVelocity = v;
    }

    // ---------------------------------------------------------------- 重力
    void ApplyGravity(float dt)
    {
        Vector2 v = _rb.linearVelocity;

        // 站在地上不累积重力，避免调试面板里速度一直涨
        if (_grounded && v.y <= 0f)
        {
            v.y = 0f;
            _rb.linearVelocity = v;
            return;
        }

        // 非对称重力：上升轻、下落重
        float g = (v.y > 0f ? gravityUp : gravityDown) / PixelsPerUnit;
        v.y = Mathf.Max(v.y - g * dt, -maxFallSpeed / PixelsPerUnit);
        _rb.linearVelocity = v;
    }

    // ---------------------------------------------------------------- 跳跃
    void ApplyJump()
    {
        if (_jumpBufferTimer <= 0f) return;

        if (_coyoteTimer > 0f)
        {
            _coyoteTimer = 0f;
            DoJump(false);
        }
        else if (_airJumpsLeft > 0)
        {
            _airJumpsLeft--;
            DoJump(true);
        }
    }

    void DoJump(bool isDouble)
    {
        _jumpBufferTimer = 0f;
        _peakFallSpeed = 0f;   // 起跳时清空，免得把起跳前的下落速度带进这次腾空
        float speed = (isDouble ? doubleJumpVelocity : jumpVelocity) / PixelsPerUnit;
        _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, speed);

        AudioManager.Play(isDouble ? "sfx_double_jump" : "sfx_jump");
    }

    // ---------------------------------------------------------------- 冲刺
    void TryDash()
    {
        if (!enableDash || _dashCooldownTimer > 0f) return;

        _dashDirection = Mathf.Abs(_moveInput) > 0.01f ? (_moveInput > 0f ? 1 : -1) : _facing;
        _dashTimer = dashTime;
        _dashCooldownTimer = dashCooldown;

        AudioManager.Play("sfx_dash");
    }

    void ApplyDash(float dt)
    {
        _rb.linearVelocity = new Vector2(_dashDirection * (dashSpeed / PixelsPerUnit), 0f);
        _dashTimer -= dt;
        if (_dashTimer <= 0f)
        {
            _dashTimer = 0f;
            // 冲刺结束后保留跑速，衔接跑动更顺
            _rb.linearVelocity = new Vector2(_dashDirection * (maxSpeed / PixelsPerUnit), _rb.linearVelocity.y);
        }
    }

    // ---------------------------------------------------------------- 计时与检测
    void TickTimers(float dt)
    {
        _jumpBufferTimer = Mathf.Max(_jumpBufferTimer - dt, 0f);
        _dashCooldownTimer = Mathf.Max(_dashCooldownTimer - dt, 0f);
        if (_hurtTimer > 0f) _hurtTimer = Mathf.Max(_hurtTimer - dt, 0f);
        if (!_grounded)
        {
            _coyoteTimer = Mathf.Max(_coyoteTimer - dt, 0f);
        }
    }

    // ---------------------------------------------------------------- 受伤
    /// <summary>
    /// 扣血 + 击退 + 硬直。返回 true 表示这次伤害真的生效了
    /// （无敌帧内、或者已经死了，都会返回 false）。
    /// 敌人撞到玩家时调用的就是这里。
    /// </summary>
    public bool TakeDamage(int amount, Vector2 sourcePosition)
    {
        Health health = HealthComponent;
        if (health == null)
        {
            Debug.LogWarning("[PlayerController] 玩家身上没有 Health 组件，无法受伤。" +
                             "在 TestLevelBootstrap 里会给玩家加上，如果你手动搭场景记得自己加一个。", this);
            return false;
        }

        if (!health.TakeDamage(amount, sourcePosition)) return false;

        AudioManager.Play("sfx_hurt");

        // 从伤害来源的反方向推开
        float dirX = transform.position.x >= sourcePosition.x ? 1f : -1f;
        _rb.linearVelocity = new Vector2(dirX * knockbackSpeedX / PixelsPerUnit,
                                         knockbackSpeedY / PixelsPerUnit);

        _hurtTimer = hurtTime;
        _dashTimer = 0f;             // 打断冲刺
        _jumpBufferTimer = 0f;       // 丢掉还没被消费的跳跃输入
        _peakFallSpeed = 0f;
        _rangedAmmo = rangedMaxAmmo; // 挨打之后弹药回满，算是被打的补偿
        _regenTimer = 0f;
        return true;
    }

    // ---------------------------------------------------------------- 冲刺的无敌与穿人
    /// <summary>
    /// 冲刺期间：给玩家无敌帧，并且把和敌人的碰撞临时关掉（可以直接穿过去）。
    /// 每帧调用，自己判断状态变化，外部不用管。
    /// </summary>
    void UpdateDashInvincibility()
    {
        bool dashing = _dashTimer > 0f;

        if (dashing)
        {
            Health health = HealthComponent;
            // 每帧续一小段，覆盖整个冲刺。用 GrantInvincibility 而不是直接改标志位，
            // 这样它和"受伤无敌帧""弹刀无敌"共用一个计时体系，不会互相覆盖。
            if (health != null) health.GrantInvincibility(Time.fixedDeltaTime * 2f);
        }

        // 只在状态切换时才做（查找敌人是有开销的）
        if (dashing != _dashPassThroughActive)
        {
            SetDashPassThrough(dashing);
        }

        // ---- 兜底自检 ----
        // 冲刺穿人 = 玩家和所有敌人的碰撞都被 IgnoreCollision 关掉了。
        // 它要是卡在"开"的状态，后果是：玩家穿过敌人、而且【接触伤害完全不会触发】——
        // 表现是"敌人碰到我却不掉血"，但代码里根本找不到扣血那一段有问题，极难查。
        // 所以这里主动检测：冲刺早就结束了，穿人状态却还开着，就强制恢复。
        if (_dashPassThroughActive && _dashTimer <= 0f)
        {
            _passThroughStuckTimer += Time.fixedDeltaTime;
            if (_passThroughStuckTimer > 0.5f)
            {
                _passThroughStuckTimer = 0f;
                SetDashPassThrough(false);
                Debug.LogWarning("[PlayerController] 检测到「冲刺穿人」状态卡住了（冲刺已结束但碰撞仍是忽略的），" +
                                 "已强制恢复。不修的话，玩家会穿过所有敌人并且不受任何接触伤害。", this);
            }
        }
        else
        {
            _passThroughStuckTimer = 0f;
        }
    }

    void SetDashPassThrough(bool on)
    {
        _dashPassThroughActive = on;

        if (on)
        {
            if (_box == null) return;

            EnemyPatrol[] enemies = FindObjectsByType<EnemyPatrol>(FindObjectsSortMode.None);
            for (int i = 0; i < enemies.Length; i++)
            {
                Collider2D[] cols = enemies[i].GetComponents<Collider2D>();
                for (int c = 0; c < cols.Length; c++)
                {
                    Collider2D col = cols[c];
                    if (col == null || col == _box) continue;

                    // IgnoreCollision 对 Trigger 也生效 —— 所以冲刺时敌人的接触伤害
                    // 连触发都不会触发，比只给无敌帧更干净。
                    Physics2D.IgnoreCollision(_box, col, true);
                    _dashIgnoredColliders.Add(col);
                }
            }
        }
        else
        {
            for (int i = 0; i < _dashIgnoredColliders.Count; i++)
            {
                Collider2D col = _dashIgnoredColliders[i];
                if (col != null && _box != null)
                {
                    Physics2D.IgnoreCollision(_box, col, false);
                }
            }
            _dashIgnoredColliders.Clear();
        }
    }

    void OnDisable()
    {
        // 兜底：万一冲刺途中组件被禁用，别把碰撞关系永久留在"忽略"状态
        if (_dashPassThroughActive) SetDashPassThrough(false);
    }

    /// <summary>
    /// 在脚下打一个小方框，看有没有踩到东西。会忽略玩家自己身上的所有碰撞体。
    ///
    /// 【检测框为什么要窄、要低】贴着墙的时候，这个框如果够宽、或者向上探进身体，
    /// 就会碰到旁边的【墙】，于是被判成"站在地上"。后果很直接：
    /// ApplyGravity 里 `if (_grounded && v.y &lt;= 0) v.y = 0;` 会把垂直速度每帧清零，
    /// 玩家就【粘在墙上下不来】，而且跟摩擦力没关系。
    ///
    /// 所以这里：宽度收到 70%（墙离身体边缘还有余量），整体压在脚底以下（不向上探）。
    /// </summary>
    bool CheckGrounded()
    {
        if (_box == null) return false;

        Bounds b = _box.bounds;

        float width = Mathf.Max(b.size.x * groundCheckWidthRatio, 0.02f);
        Vector2 origin = new Vector2(b.center.x, b.min.y - groundCheckHeight * 0.5f);
        Vector2 size = new Vector2(width, groundCheckHeight);

        Collider2D[] hits = Physics2D.OverlapBoxAll(origin, size, 0f, groundMask);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D h = hits[i];
            if (h == null || h == _box || h.isTrigger) continue;
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            return true;
        }
        return false;
    }

    void UpdateState()
    {
        if (_hurtTimer > 0f) CurrentState = State.Hurt;
        else if (_dashTimer > 0f) CurrentState = State.Dash;
        else if (!_grounded) CurrentState = _rb.linearVelocity.y > 0f ? State.Jump : State.Fall;
        else if (Mathf.Abs(_rb.linearVelocity.x) > 0.3f) CurrentState = State.Run;
        else CurrentState = State.Idle;
    }

    // 在 Scene 视图里把脚下那个检测框画出来，方便确认它有没有对准地面。
    // 【必须和 CheckGrounded() 里用的是同一组数值】——
    // 不一致的话这个 Gizmo 会画出一个假的框，比没有还糟。
    void OnDrawGizmosSelected()
    {
        var box = GetComponent<BoxCollider2D>();
        if (box == null) return;

        Bounds b = box.bounds;
        float width = Mathf.Max(b.size.x * groundCheckWidthRatio, 0.02f);

        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
        Gizmos.DrawWireCube(
            new Vector3(b.center.x, b.min.y - groundCheckHeight * 0.5f, 0f),
            new Vector3(width, groundCheckHeight, 0f));
    }
}
