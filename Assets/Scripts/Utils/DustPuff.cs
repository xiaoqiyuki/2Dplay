using UnityEngine;

/// <summary>
/// 一小块尘土：出生时给一个初速度，之后受重力、逐渐淡出，寿命到了自毁。
///
/// 【单位】速度用世界单位/秒（不是像素），因为这个脚本内部用不着和像素打交道。
/// </summary>
public class DustPuff : MonoBehaviour
{
    /// <summary>初速度（世界单位/秒）</summary>
    public Vector2 velocity;
    /// <summary>重力（世界单位/秒²），负值向下</summary>
    public float gravity = -14f;
    /// <summary>寿命（秒）</summary>
    public float lifetime = 0.35f;
    /// <summary>寿命过了这个比例之后开始淡出（0~1）</summary>
    public float fadeStart = 0.45f;

    SpriteRenderer _sr;
    Color _baseColor;
    float _age;

    void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
        if (_sr != null) _baseColor = _sr.color;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        _age += dt;

        if (_age >= lifetime || _sr == null)
        {
            Destroy(gameObject);
            return;
        }

        velocity.y += gravity * dt;
        transform.position += (Vector3)(velocity * dt);

        // 淡出
        float t = _age / lifetime;
        float alpha = t <= fadeStart ? 1f : 1f - Mathf.InverseLerp(fadeStart, 1f, t);
        Color c = _baseColor;
        c.a = _baseColor.a * alpha;
        _sr.color = c;
    }
}
