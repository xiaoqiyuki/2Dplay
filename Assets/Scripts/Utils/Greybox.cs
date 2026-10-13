using UnityEngine;

/// <summary>
/// 灰盒阶段的占位素材工厂：运行时生成纯色方块 Sprite。
///
/// 尺寸按「像素」给，内部按 PixelsPerUnit = 16 生成，和 Pixel Perfect Camera 的
/// Assets Pixels Per Unit 对齐，所以物体不需要对 Transform 做任何缩放。
/// </summary>
public static class Greybox
{
    /// <summary>1 个世界单位对应多少像素。必须和 Sprite 的 Pixels Per Unit 一致。</summary>
    public const float PixelsPerUnit = 16f;

    /// <summary>生成一张纯色 Sprite，像素尺寸由参数决定。</summary>
    public static Sprite Solid(Color color, int widthPixels, int heightPixels)
    {
        widthPixels = Mathf.Max(1, widthPixels);
        heightPixels = Mathf.Max(1, heightPixels);

        var tex = new Texture2D(widthPixels, heightPixels, TextureFormat.RGBA32, false);
        tex.name = "greybox_" + widthPixels + "x" + heightPixels;
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        var pixels = new Color[widthPixels * heightPixels];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = color;
        }
        tex.SetPixels(pixels);
        tex.Apply();

        var sprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, widthPixels, heightPixels),
            new Vector2(0.5f, 0.5f),
            PixelsPerUnit,
            0,
            SpriteMeshType.FullRect);
        sprite.name = tex.name;
        return sprite;
    }

    /// <summary>
    /// 建一个方块物体。坐标和尺寸都用像素，collider=true 时附带一个尺寸完全吻合的 BoxCollider2D。
    /// </summary>
    public static GameObject CreateBox(string name, Vector2 centerPixels, Vector2 sizePixels,
                                       Color color, bool collider, Transform parent)
    {
        var go = new GameObject(name);
        if (parent != null)
        {
            go.transform.SetParent(parent, false);
        }
        go.transform.position = new Vector3(centerPixels.x / PixelsPerUnit, centerPixels.y / PixelsPerUnit, 0f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Solid(color, Mathf.RoundToInt(sizePixels.x), Mathf.RoundToInt(sizePixels.y));
        sr.sortingOrder = 0;

        if (collider)
        {
            var bc = go.AddComponent<BoxCollider2D>();
            bc.size = sizePixels / PixelsPerUnit;
        }
        return go;
    }
}
