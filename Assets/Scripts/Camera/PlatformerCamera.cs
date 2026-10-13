using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// 横版闯关相机：平滑跟随 + 朝跑动方向前瞻 + 关卡边界限制 + 可选像素吸附。
///
/// 【为什么不用 Cinemachine】
///   1. 少一个包依赖。Cinemachine 要单独装，一旦没装，整份代码就编译不过 —— 而你已经在这类
///      "环境没配对"的问题上卡过好几次了。
///   2. 像素游戏对相机位置很敏感，尤其是"什么时候把相机吸附到像素网格"这件事，
///      自己写才好精确控制。
///   3. 这套逻辑一共一百多行，改起来比调 Cinemachine 一堆组件的参数快得多。
///   等以后要做镜头演出（震屏、转场、Boss 入场）时再换 Cinemachine 也不迟。
///
/// 【单位】所有可调数值都用「像素」，内部按 PixelsPerUnit 换算成 Unity 单位。
///
/// 【重要】相机不能是玩家的子物体，否则没法做平滑跟随（子物体会被父物体硬拽着走）。
/// 脚本所在的 TestLevelBootstrap 会自动把它挪到场景根节点。
///
/// 【它也能独立工作】如果你没挂 TestLevelBootstrap（比如自己搭正式关卡），
/// 只把这个脚本挂到 Main Camera 上就行：
///   · Target 留空 → 它会自己找场景里的 PlayerController
///   · 正交开关和视野大小 → 它会自己按 Reference Height 配好
/// </summary>
[RequireComponent(typeof(Camera))]
public class PlatformerCamera : MonoBehaviour
{
    [Header("跟随目标")]
    [Tooltip("留空会在运行时自动找场景里的 PlayerController")]
    public Transform target;

    [Header("相机视野")]
    [Tooltip("基准分辨率高度（像素）。orthographicSize = 这个值 ÷ 2 ÷ PixelsPerUnit")]
    public int referenceHeight = 360;
    [Tooltip("开机时强制把相机设成正交并套用上面的视野。如果你有特殊需求可以关掉")]
    public bool forceOrthographic = true;

    [Header("跟随手感")]
    [Tooltip("水平跟随的平滑时间，越大越\"拖\"")]
    public float smoothTimeX = 0.16f;
    [Tooltip("垂直跟随的平滑时间")]
    public float smoothTimeY = 0.28f;

    [Header("前瞻：朝跑动方向多看一点")]
    [Tooltip("用「速度 × 这个时长」来算前瞻距离")]
    public float lookAheadTime = 0.35f;
    [Tooltip("前瞻距离上限（像素）")]
    public float maxLookAhead = 56f;
    [Tooltip("前瞻偏移的平滑时间，调大则镜头转向更慢更稳")]
    public float lookAheadSmoothTime = 0.30f;

    [Header("垂直死区")]
    [Tooltip("玩家离镜头中心在这个范围内时，垂直方向不跟，避免每次小跳都上下晃镜头。单位：像素")]
    public float verticalDeadZone = 40f;

    [Header("关卡边界（像素，世界坐标）")]
    [Tooltip("镜头不会拍到这块矩形之外，避免看到关卡外面的空白")]
    public bool useBounds = true;
    [Tooltip("勾上则开机时自动从场景里的 Tilemap 算边界（用 Tilemap 搭关卡时用这个）。" +
             "场景里没有 Tilemap 时会退回下面手填的数值")]
    public bool autoBoundsFromTilemap = true;
    [Tooltip("只用【带碰撞体】的 Tilemap 算边界（推荐）。" +
             "纯背景/装饰层往往铺得比关卡大，算进来会把镜头能拍到的范围撑得很大")]
    public bool boundsOnlyFromCollidingTilemaps = true;
    public Vector2 boundsMin = new Vector2(-260f, -120f);
    public Vector2 boundsMax = new Vector2(1000f, 420f);

    [Header("像素吸附")]
    [Tooltip("把镜头最终位置吸附到像素网格。如果画面移动时有\"抖\"的感觉，打开试试")]
    public bool pixelSnap = false;

    Camera _cam;
    Rigidbody2D _targetBody;
    PlayerController _player;
    Vector2 _smoothVelocity;
    Vector2 _current;
    float _lookAhead;
    float _lookAheadVelocity;

    float _shakeTimer;
    float _shakeDuration;
    float _shakeMagnitude;

    float _autoBindTimer;

    void Awake()
    {
        _cam = GetComponent<Camera>();

        // 自己把相机配好，不依赖 TestLevelBootstrap。
        // 这样你只把本脚本挂到 Main Camera 上也能得到正确的像素视野。
        if (forceOrthographic && _cam != null)
        {
            _cam.orthographic = true;
            _cam.orthographicSize = referenceHeight / 2f / PlayerController.PixelsPerUnit;  // 360/2/16 = 11.25
        }

        if (autoBoundsFromTilemap)
        {
            TryComputeBoundsFromTilemaps();
        }

        BindTargetIfPossible();
    }

    /// <summary>
    /// 绑定跟随目标。TestLevelBootstrap 会在创建完玩家之后直接赋值给 target，
    /// 所以这里的自动查找主要是给"没有 Bootstrap、自己搭场景"的情况兜底。
    /// </summary>
    void BindTargetIfPossible()
    {
        if (target != null)
        {
            target.TryGetComponent(out _targetBody);
            target.TryGetComponent(out _player);
            _current = new Vector2(target.position.x, target.position.y);
            ApplyTransform(Vector2.zero);
            return;
        }

        PlayerController pc = FindFirstObjectByType<PlayerController>();
        if (pc == null) return;

        target = pc.transform;
        target.TryGetComponent(out _targetBody);
        _player = pc;
        _current = new Vector2(target.position.x, target.position.y);
        ApplyTransform(Vector2.zero);

        Debug.Log("[PlatformerCamera] Target 是空的，已自动绑定到场景里的玩家。", this);
    }

    /// <summary>
    /// 用场景里 Tilemap 的合并包围盒当作镜头边界。
    /// 这样关卡画到哪，镜头就自动跟着放到哪，不用回来手填数字。
    ///
    /// 默认只算【带碰撞体】的 Tilemap —— 因为"玩家能去的地方"是由实心地形定义的。
    /// 纯背景/装饰层往往铺得比关卡大得多，把它算进来会把镜头边界撑得巨大，
    /// 于是镜头能拍到关卡外面一大片空白。
    /// </summary>
    void TryComputeBoundsFromTilemaps()
    {
        TilemapRenderer[] maps = FindObjectsByType<TilemapRenderer>(FindObjectsSortMode.None);
        if (maps == null || maps.Length == 0)
        {
            Debug.Log("[PlatformerCamera] 场景里没有 Tilemap，沿用 Inspector 里手填的边界。", this);
            return;
        }

        bool has = false;
        Bounds total = default;
        int used = 0;
        int skippedNoCollider = 0;

        for (int i = 0; i < maps.Length; i++)
        {
            if (maps[i] == null) continue;

            if (boundsOnlyFromCollidingTilemaps)
            {
                // 带碰撞体 = 玩家真的能站/能碰到，才算作关卡范围
                if (maps[i].GetComponent<TilemapCollider2D>() == null)
                {
                    skippedNoCollider++;
                    continue;
                }
            }

            Bounds b = maps[i].bounds;
            if (b.size.x <= 0.0001f || b.size.y <= 0.0001f) continue;   // 空 Tilemap 跳过

            if (!has) { total = b; has = true; }
            else total.Encapsulate(b);
            used++;
        }

        if (!has)
        {
            Debug.Log("[PlatformerCamera] 没有可用的 Tilemap（都不带碰撞体或都是空的），" +
                      "沿用 Inspector 里手填的边界。", this);
            return;
        }

        const float ppu = PlayerController.PixelsPerUnit;
        boundsMin = new Vector2(total.min.x * ppu, total.min.y * ppu);
        boundsMax = new Vector2(total.max.x * ppu, total.max.y * ppu);

        Debug.Log(string.Format(
            "[PlatformerCamera] 已从 {0} 个带碰撞的 Tilemap 自动算出镜头边界：({1:0},{2:0}) 到 ({3:0},{4:0}) 像素" +
            "（跳过了 {5} 个纯背景层）",
            used, boundsMin.x, boundsMin.y, boundsMax.x, boundsMax.y, skippedNoCollider), this);

        // 顺便算一下"镜头实际能移动多少像素"。
        // 关卡只比视口宽一点点时，镜头几乎没有活动空间 —— 看起来就像"镜头固定不动"，
        // 但其实是关卡太小，不是 bug。这种情况必须提醒，否则会一直以为是代码问题。
        if (_cam != null)
        {
            float halfWpx = _cam.orthographicSize * _cam.aspect * ppu;
            float halfHpx = _cam.orthographicSize * ppu;

            float rangeX = (boundsMax.x - boundsMin.x) - halfWpx * 2f;
            float rangeY = (boundsMax.y - boundsMin.y) - halfHpx * 2f;

            string msg = string.Format(
                "[PlatformerCamera] 镜头可移动范围：水平 {0:0} 像素、垂直 {1:0} 像素", rangeX, rangeY);

            if (rangeX < 32f || rangeY < 32f)
            {
                Debug.LogWarning(msg + "\n关卡比视口大不了多少，所以镜头几乎不会移动 —— " +
                                 "这不是代码问题，是关卡（或者它的边界）太小了。\n" +
                                 "想确认的话：Play 模式下取消勾选本组件的 Use Bounds，镜头就会自由跟随。", this);
            }
            else
            {
                Debug.Log(msg, this);
            }
        }
    }

    void LateUpdate()
    {
        // 用 LateUpdate 而不是 Update：必须等玩家这一帧移动完再动镜头，否则会落后一帧
        if (_cam == null) return;

        // 目标还没绑上就隔一会儿再试一次（玩家可能是后创建的）
        if (target == null)
        {
            _autoBindTimer -= Time.deltaTime;
            if (_autoBindTimer <= 0f)
            {
                _autoBindTimer = 0.5f;
                BindTargetIfPossible();
            }
            if (target == null) return;
        }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        const float ppu = PlayerController.PixelsPerUnit;

        // ---- 前瞻：跑得越快，往跑动方向看得越远 ----
        float vx = _targetBody != null ? _targetBody.linearVelocity.x : 0f;
        float desiredLookAhead = Mathf.Clamp(vx * lookAheadTime * ppu, -maxLookAhead, maxLookAhead);
        _lookAhead = Mathf.SmoothDamp(_lookAhead, desiredLookAhead,
                                      ref _lookAheadVelocity, lookAheadSmoothTime, Mathf.Infinity, dt);

        // ---- 水平：始终跟随 ----
        float desiredX = target.position.x + _lookAhead / ppu;
        _current.x = Mathf.SmoothDamp(_current.x, desiredX,
                                      ref _smoothVelocity.x, smoothTimeX, Mathf.Infinity, dt);

        // ---- 垂直：落地时跟随；腾空时只有超出死区才跟 ----
        bool grounded = _player == null || _player.IsGrounded;
        float dyPx = Mathf.Abs(target.position.y - _current.y) * ppu;
        if (grounded || dyPx > verticalDeadZone)
        {
            _current.y = Mathf.SmoothDamp(_current.y, target.position.y,
                                          ref _smoothVelocity.y, smoothTimeY, Mathf.Infinity, dt);
        }

        // ---- 关卡边界 ----
        if (useBounds)
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * _cam.aspect;
            _current.x = ClampAxis(_current.x, boundsMin.x / ppu + halfW, boundsMax.x / ppu - halfW);
            _current.y = ClampAxis(_current.y, boundsMin.y / ppu + halfH, boundsMax.y / ppu - halfH);
        }

        // ---- 像素吸附 ----
        if (pixelSnap)
        {
            _current.x = Mathf.Round(_current.x * ppu) / ppu;
            _current.y = Mathf.Round(_current.y * ppu) / ppu;
        }

        // ---- 屏幕震动 ----
        // 注意：震动是加在最终位置上的"临时偏移"，不会污染 _current。
        // 否则每帧的随机抖动会被 SmoothDamp 累积进去，镜头会越震越飘。
        Vector2 shake = Vector2.zero;
        if (_shakeTimer > 0f)
        {
            _shakeTimer = Mathf.Max(_shakeTimer - dt, 0f);

            // 按剩余时间线性衰减，震动不会"啪"地一下停住
            float falloff = _shakeTimer / Mathf.Max(_shakeDuration, 0.0001f);
            float amp = _shakeMagnitude * falloff / ppu;
            shake = new Vector2(Random.Range(-amp, amp), Random.Range(-amp, amp));
        }

        ApplyTransform(shake);
    }

    /// <summary>
    /// 请求一次屏幕震动。参数用像素。
    /// 连续命中时只取较强的一次 —— 直接叠加会把镜头震成癫痫。
    /// </summary>
    public void Shake(float duration, float magnitudePixels)
    {
        if (duration <= 0f || magnitudePixels <= 0f) return;
        if (_shakeTimer > 0f && _shakeMagnitude >= magnitudePixels) return;

        _shakeDuration = duration;
        _shakeTimer = duration;
        _shakeMagnitude = magnitudePixels;
    }

    void ApplyTransform(Vector2 shakeOffset)
    {
        transform.position = new Vector3(_current.x + shakeOffset.x,
                                         _current.y + shakeOffset.y,
                                         transform.position.z);
    }

    /// <summary>关卡比视口还窄/矮时 min 会大于 max，这时没法夹取，直接取中点居中。</summary>
    static float ClampAxis(float value, float min, float max)
    {
        if (min > max) return (min + max) * 0.5f;
        return Mathf.Clamp(value, min, max);
    }

    // 在 Scene 视图里把边界画出来，方便调整
    void OnDrawGizmosSelected()
    {
        if (!useBounds) return;

        const float ppu = PlayerController.PixelsPerUnit;
        Vector3 a = new Vector3(boundsMin.x / ppu, boundsMin.y / ppu, 0f);
        Vector3 b = new Vector3(boundsMax.x / ppu, boundsMax.y / ppu, 0f);

        Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
        Gizmos.DrawWireCube((a + b) * 0.5f,
                            new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), 0f));
    }
}
