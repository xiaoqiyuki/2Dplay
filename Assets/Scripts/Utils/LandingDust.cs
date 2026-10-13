using UnityEngine;

/// <summary>
/// 落地尘土：挂在玩家身上，订阅 PlayerController.Landed 事件，落地时在脚底扬起几块小方块。
///
/// 【为什么不用 Particle System】
///   1. 粒子系统需要一个材质（Material），而材质要引用一个 shader。URP 下用 Shader.Find
///      有时能在编辑器里跑、打包后却变白 —— 这类"编辑器好好的、打包就坏"的问题最难查。
///   2. 像素游戏里"几块手搓的小方块"其实比柔和的粒子更对味。
///   3. 不需要任何贴图和材质资源，代码跑起来就有。
///
/// 【单位】参数用「像素/秒」，内部换算成世界单位。
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class LandingDust : MonoBehaviour
{
    [Header("开关")]
    public bool enable = true;

    [Header("触发门槛")]
    [Tooltip("下落速度低于这个值就不扬尘（像素/秒）。" +
             "参考：一格（16px）高度掉下来约 226 px/s，一次普通跳跃落地约 396 px/s")]
    public float minFallSpeed = 100f;
    [Tooltip("达到这个下落速度时尘土量最大（像素/秒）")]
    public float maxFallSpeed = 420f;

    [Header("尘土外观")]
    public Color color = new Color(0.85f, 0.86f, 0.92f, 0.9f);
    [Tooltip("尘土的像素尺寸")]
    public int puffPixelSize = 2;
    [Tooltip("单次最少 / 最多的尘土块数")]
    public int minCount = 3;
    public int maxCount = 6;

    [Header("尘土运动（像素/秒）")]
    public Vector2 speedXRange = new Vector2(30f, 75f);
    public Vector2 speedYRange = new Vector2(25f, 65f);
    public float puffLifetime = 0.35f;

    PlayerController _player;
    BoxCollider2D _box;
    Sprite _puffSprite;

    void Awake()
    {
        _player = GetComponent<PlayerController>();
        _box = GetComponent<BoxCollider2D>();

        // 只生成一次贴图，所有尘土共用它 —— 每次落地都新建贴图会漏内存
        _puffSprite = Greybox.Solid(Color.white, puffPixelSize, puffPixelSize);
    }

    void OnEnable()
    {
        if (_player != null) _player.Landed += OnLanded;
    }

    void OnDisable()
    {
        if (_player != null) _player.Landed -= OnLanded;
    }

    void OnLanded(float fallSpeed)
    {
        if (!enable || _box == null) return;

        // PlayerController.Landed 传进来的是世界单位/秒，换算成像素再比
        float fallSpeedPx = fallSpeed * PlayerController.PixelsPerUnit;
        if (fallSpeedPx < minFallSpeed) return;

        float t = Mathf.InverseLerp(minFallSpeed, maxFallSpeed, fallSpeedPx);
        int count = Mathf.RoundToInt(Mathf.Lerp(minCount, maxCount, t));

        Bounds b = _box.bounds;
        for (int i = 0; i < count; i++)
        {
            SpawnPuff(b, t);
        }
    }

    void SpawnPuff(Bounds bodyBounds, float strength)
    {
        var go = new GameObject("DustPuff");
        go.transform.SetParent(transform.parent, false);
        go.transform.position = new Vector3(
            Random.Range(bodyBounds.min.x, bodyBounds.max.x),
            bodyBounds.min.y,
            0f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _puffSprite;
        sr.color = color;
        // 比玩家靠后一点画，免得挡住角色
        sr.sortingOrder = 5;

        float dir = Random.value < 0.5f ? -1f : 1f;

        var puff = go.AddComponent<DustPuff>();
        puff.velocity = new Vector2(
            dir * Random.Range(speedXRange.x, speedXRange.y) / PlayerController.PixelsPerUnit,
            Random.Range(speedYRange.x, speedYRange.y) / PlayerController.PixelsPerUnit);
        puff.gravity = -14f * Mathf.Lerp(0.8f, 1.3f, strength);
        puff.lifetime = puffLifetime;
    }
}
