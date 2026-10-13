using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// 直接从 Tilemap 的格子生成碰撞体 —— 完全不用 TilemapCollider2D。
///
/// 【为什么要这么干】
/// 打包版里 TilemapCollider2D 是死的：
///   · 格子数据正常（GetTile 能读到 2466 个 colliderType=Grid 的 Tile）
///   · 组件在、enabled=true、merge 设置正确
///   · 但它的 bounds 一直是 (0,0,0)，一个形状都没生成
///   · 试过 ProcessTilemapChanges() / SetTile / RefreshAllTiles() / GenerateGeometry()
///     —— 四道强制重建全部无效，前后一模一样
///   · 而编辑器和打包版行为不一致，只有打包版坏
///
/// 与其继续在那个组件上耗，不如绕开它：读格子 → 自己合并成矩形 → 生成 BoxCollider2D。
/// 这条路每一步我都能推演，不依赖任何"Unity 内部应该会自己刷新"的假设。
///
/// 【贪心矩形合并】
/// 逐格扫描，遇到实心格就往右、往下尽量扩，扩到不能再扩就吐出一个矩形。
/// 好处不只是"能work"：786×21 的地面用逐格碰撞体是 2466 个形状，
/// 合并之后通常只剩几十到几百个，物理开销反而更低。
/// </summary>
public class GridColliderBuilder : MonoBehaviour
{
    [Tooltip("生成之后把原来的 TilemapCollider2D / CompositeCollider2D 关掉。\n" +
             "必须关 —— 否则编辑器里（那两个组件是好的）会和这里生成的碰撞体重叠。")]
    public bool disableOriginalColliders = true;

    [Tooltip("打印每个 Tilemap 生成了多少个矩形")]
    public bool logDetails = true;

    /// <summary>
    /// 给场景里所有 Tilemap 生成碰撞体。
    /// 返回生成的碰撞体总数。
    /// </summary>
    public static int Build(Transform parent, bool disableOriginals, bool log)
    {
        Tilemap[] maps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
        int totalColliders = 0;

        for (int i = 0; i < maps.Length; i++)
        {
            Tilemap map = maps[i];
            if (map == null) continue;

            // 先看这层有没有实心格 / 尖刺格，没有就跳过（背景层会在这里被跳过）
            List<RectInt> solids = MergeRects(map, false);
            List<RectInt> hazards = MergeRects(map, true);

            if (solids.Count == 0 && hazards.Count == 0) continue;

            // ⚠️ 碰撞体必须建在【原来的 Tilemap 物体】上，不能新建子物体。
            //
            // 我第一版新建了一个 "XXX_Colliders" 子物体，结果【所有尖刺都失效了】：
            // 2D 触发器回调只送给【碰撞体所在的那个物体】，
            // 而 HazardZone 挂在 Hazard 这个 Tilemap 上 ——
            // 碰撞体跑到子物体之后，HazardZone 的 OnTriggerStay2D 永远不会被调用。
            // 不报错、不崩溃，就是尖刺静悄悄地不掉血。
            GameObject go = map.gameObject;

            int made = 0;
            made += Attach(go, map, solids, false);
            made += Attach(go, map, hazards, true);

            // 一个都没生成的话，原来的组件千万别动 —— 动了下场就是整个地面没碰撞
            if (made == 0) continue;

            // 原来的组件必须【销毁】，不能只禁用：
            //   · Rigidbody2D 若只是 simulated=false，物体上【所有】碰撞体都不进物理世界，
            //     包括我新加的这些 —— 那就等于白干
            //   · CompositeCollider2D 即使禁用，也可能影响新碰撞体
            // 先禁用再销毁，双保险（Destroy 是延迟到帧末的）。
            if (disableOriginals)
            {
                TilemapCollider2D tc = map.GetComponent<TilemapCollider2D>();
                if (tc != null) { tc.enabled = false; Destroy(tc); }

                CompositeCollider2D cc = map.GetComponent<CompositeCollider2D>();
                if (cc != null) { cc.enabled = false; Destroy(cc); }

                Rigidbody2D rb = map.GetComponent<Rigidbody2D>();
                if (rb != null) { rb.simulated = false; Destroy(rb); }
            }

            totalColliders += made;

            if (log)
            {
                Debug.Log(string.Format(
                    "[GridColliderBuilder] {0}：实心格合并成 {1} 个矩形、尖刺 {2} 个矩形，" +
                    "共 {3} 个碰撞体（建在 {0} 自身上，HazardZone 这类脚本才收得到触发器回调）",
                    map.name, solids.Count, hazards.Count, made), map);
            }
        }

        return totalColliders;
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 贪心合并：把格子合并成尽量少的大矩形。
    /// wantTrigger=true 时收集 ColliderType=Sprite 的格（尖刺），否则收集 Grid 的格（地面）。
    /// </summary>
    static List<RectInt> MergeRects(Tilemap map, bool wantTrigger)
    {
        // 1. 先把符合条件、且不是空的格子收进集合
        var cells = new HashSet<Vector2Int>();
        BoundsInt bounds = map.cellBounds;

        foreach (Vector3Int pos in bounds.allPositionsWithin)
        {
            TileBase tb = map.GetTile(pos);
            if (tb == null) continue;

            var tile = tb as Tile;
            if (tile == null) continue;

            // Grid = 整格实心（地面/平台/墙）；Sprite = 按精灵轮廓（尖刺）
            bool isGrid = tile.colliderType == Tile.ColliderType.Grid;
            bool isSprite = tile.colliderType == Tile.ColliderType.Sprite;

            if (wantTrigger ? isSprite : isGrid)
            {
                cells.Add(new Vector2Int(pos.x, pos.y));
            }
        }

        var rects = new List<RectInt>();
        if (cells.Count == 0) return rects;

        // 2. 贪心扩矩形
        var used = new HashSet<Vector2Int>();

        // 排序只是为了让结果稳定（同样的场景每次生成一样的碰撞体），不是正确性需要
        var ordered = new List<Vector2Int>(cells);
        ordered.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

        for (int i = 0; i < ordered.Count; i++)
        {
            Vector2Int p = ordered[i];
            if (used.Contains(p)) continue;

            // 往右扩
            int w = 1;
            while (true)
            {
                var q = new Vector2Int(p.x + w, p.y);
                if (!cells.Contains(q) || used.Contains(q)) break;
                w++;
            }

            // 往下扩：要整行 w 个格子都可用才能加一行
            int h = 1;
            while (true)
            {
                bool rowOk = true;
                for (int x = 0; x < w; x++)
                {
                    var q = new Vector2Int(p.x + x, p.y + h);
                    if (!cells.Contains(q) || used.Contains(q)) { rowOk = false; break; }
                }
                if (!rowOk) break;
                h++;
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    used.Add(new Vector2Int(p.x + x, p.y + y));
                }
            }

            rects.Add(new RectInt(p.x, p.y, w, h));
        }

        return rects;
    }

    /// <summary>把矩形列表变成挂在 go 上的 BoxCollider2D</summary>
    static int Attach(GameObject go, Tilemap map, List<RectInt> rects, bool isTrigger)
    {
        int made = 0;

        for (int i = 0; i < rects.Count; i++)
        {
            RectInt r = rects[i];

            // CellToWorld 给的是格子的【角】，两个对角就能算出矩形的世界范围。
            // 用世界坐标而不是局部坐标，可以不管 Grid 和 Tilemap 上有什么缩放/位移。
            Vector3 a = map.CellToWorld(new Vector3Int(r.xMin, r.yMin, 0));
            Vector3 b = map.CellToWorld(new Vector3Int(r.xMax, r.yMax, 0));

            float w = Mathf.Abs(b.x - a.x);
            float h = Mathf.Abs(b.y - a.y);

            // 尺寸为 0 的矩形是无效碰撞体，直接跳过（也顺便挡住除零之类的问题）
            if (w <= 0.0001f || h <= 0.0001f) continue;

            var col = go.AddComponent<BoxCollider2D>();
            col.offset = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f) - (Vector2)go.transform.position;
            col.size = new Vector2(w, h);
            col.isTrigger = isTrigger;

            // 注意：BoxCollider2D 【没有】 direction 属性 ——
            // direction / BoxCollider2D.Direction 是 3D 的 BoxCollider 才有的东西。
            // 我在这里一度加过一行 `col.direction = BoxCollider2D.Direction.Out;`，
            // 注释写的是"无所谓，但显式一点"，结果整个工程编译不过。
            // 教训：为了"显式"而写的、功能上无所谓的代码，是纯粹的负债。
            made++;
        }

        return made;
    }
}
