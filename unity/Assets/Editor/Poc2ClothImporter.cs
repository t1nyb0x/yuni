// PoC-2 Step 4: 抽出した MagicaCloth v1 の中間表現(JSON)を読み、
// SPCR JointDynamics のコンポーネントを Unity Editor 上で組む。
//
// これは PoC の使い捨てコードである。本実装へ持ち込まないこと。
// docs/poc/poc-2.md / docs/adr/0003 を参照。
//
// 設計上の要点
// ------------
// * ボーンの解決は fileID で行う。名前では引き当てない（同名オブジェクトが存在する）。
//   シーン上のインスタンスから PrefabUtility.GetCorrespondingObjectFromOriginalSource で
//   FBX 側のオブジェクトへ遡り、その localFileId を突き合わせる。
// * カプセルの向きの規約が v1 と SPCR で違う。
//   v1  : axis フィールド(0=X,1=Y,2=Z)で指定
//   SPCR: 常に transform.up (Y) 方向
//   そのため回転を補正した新しい GameObject を作る。既存の v1 オブジェクトは触らない。
// * SPCR はチェーン上の「全ボーン」に SPCRJointDynamicsPoint を要求する。
//   SearchPoints() が Point の無い GameObject で打ち切るため、根だけでは繋がらない。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SPCR;
using UnityEditor;
using UnityEngine;

namespace Yuni.Poc
{
    public static class Poc2ClothImporter
    {
        // ---- JSON の受け皿（抽出器 tools/poc2_extract_v1_cloth.py の出力に対応）----

        [Serializable] public class Vec3 { public float x, y, z; public Vector3 V => new Vector3(x, y, z); }
        [Serializable] public class Quat { public float x, y, z, w; public Quaternion Q => new Quaternion(x, y, z, w); }

        [Serializable]
        public class Anchor
        {
            public string kind;
            public string source_file_id;
            public string source_guid;
        }

        [Serializable]
        public class ParentRef
        {
            public string[] names_from_anchor;
            public Anchor anchor;
        }

        [Serializable]
        public class RootRef
        {
            public string kind;
            public string source_file_id;
            public string name;
        }

        [Serializable]
        public class ColliderRef
        {
            public string mono_file_id;
            public string name;
        }

        [Serializable]
        public class ColliderDef
        {
            public string name;
            public string shape;          // "capsule" | "sphere"
            public Vec3 local_position;
            public Quat local_rotation;
            public Vec3 center;
            public float axis;            // 0=X 1=Y 2=Z（capsule のみ）
            public float length;
            public float start_radius;
            public float end_radius;
            public float radius;          // sphere のみ
            public ParentRef parent;
        }

        /// v1 の BezierParam。根(start)から先端(end)へ深さで補間される。
        [Serializable]
        public class Curve
        {
            public float startValue;
            public float endValue;
            public float useEndValue;

            public float At(float rate01)
                => useEndValue > 0.5f ? Mathf.Lerp(startValue, endValue, rate01) : startValue;
        }

        [Serializable]
        public class ChainParams
        {
            public Curve radius;   // 粒子半径。これが布の厚みになる
            public Curve mass;
            public Curve gravity;
            public Vec3 gravity_direction;
        }

        [Serializable]
        public class ChainDef
        {
            public string name;
            public RootRef[] roots;
            public ColliderRef[] colliders;
            public ChainParams @params;
        }

        [Serializable]
        public class Extract
        {
            public string source;
            public ChainDef[] chains;
            public ColliderDef[] colliders;
        }

        // ---- メニュー ----

        [MenuItem("Yuni/PoC-2/スカートだけ SPCR へ変換", false, 1)]
        static void ImportSkirtOnly() => Run("Skirt");

        [MenuItem("Yuni/PoC-2/全チェーンを SPCR へ変換", false, 2)]
        static void ImportAll() => Run(null);

        [MenuItem("Yuni/PoC-2/生成した SPCR 構成を削除", false, 20)]
        static void Cleanup()
        {
            var root = Selection.activeGameObject;
            if (root == null) { EditorUtility.DisplayDialog("PoC-2", "モデルのルートを選択してください。", "OK"); return; }

            int removed = 0;
            foreach (var c in root.GetComponentsInChildren<SPCRJointDynamicsController>(true))
            { UnityEngine.Object.DestroyImmediate(c); removed++; }   // ルートに同居するのでコンポーネントだけ消す
            foreach (var p in root.GetComponentsInChildren<SPCRJointDynamicsPoint>(true))
            { UnityEngine.Object.DestroyImmediate(p); removed++; }
            foreach (var col in root.GetComponentsInChildren<SPCRJointDynamicsCollider>(true))
            { UnityEngine.Object.DestroyImmediate(col.gameObject); removed++; }

            Debug.Log($"[PoC-2] 削除しました: {removed} 件");
        }

        // ---- 本体 ----

        static void Run(string chainFilter)
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("PoC-2",
                    "Play 中は実行できません。\n\n" +
                    "Play を抜けてから変換し、そのあとで Play してください。\n" +
                    "Play 中に加えた変更は Play を抜けると消えます。", "OK");
                return;
            }

            var root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog("PoC-2", "シーン上のモデルのルートを選択してから実行してください。", "OK");
                return;
            }

            var path = EditorUtility.OpenFilePanel("抽出した中間表現 (JSON) を選択", "", "json");
            if (string.IsNullOrEmpty(path)) return;

            Extract data;
            try
            {
                data = JsonUtility.FromJson<Extract>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[PoC-2] JSON を読めませんでした: {e.Message}");
                return;
            }
            if (data?.chains == null || data.colliders == null)
            {
                Debug.LogError("[PoC-2] JSON の形式が想定と違います。");
                return;
            }

            var summary = new List<string>();
            var boneByFileId = BuildBoneMap(root.transform);
            summary.Add($"FBX Transform の解決: {boneByFileId.Count} 件");
            Debug.Log($"[PoC-2] FBX の Transform を {boneByFileId.Count} 件解決しました。");

            // --- コライダを作る ---
            var madeColliders = new Dictionary<string, SPCRJointDynamicsCollider>();
            foreach (var def in data.colliders)
            {
                var parentId = def.parent?.anchor?.source_file_id;
                if (string.IsNullOrEmpty(parentId) || !boneByFileId.TryGetValue(parentId, out var bone))
                {
                    Debug.LogWarning($"[PoC-2] 親ボーンを解決できません: {def.name} (fileID {parentId})");
                    continue;
                }

                var go = new GameObject($"SPCR Collider ({def.name})");
                Undo.RegisterCreatedObjectUndo(go, "PoC-2 collider");
                go.transform.SetParent(bone, false);

                var v1Rot = def.local_rotation?.Q ?? Quaternion.identity;
                var center = def.center?.V ?? Vector3.zero;
                go.transform.localPosition = (def.local_position?.V ?? Vector3.zero) + v1Rot * center;
                go.transform.localRotation = v1Rot * AxisFix(def.shape == "capsule" ? Mathf.RoundToInt(def.axis) : 1);
                go.transform.localScale = Vector3.one;

                var col = go.AddComponent<SPCRJointDynamicsCollider>();
                if (def.shape == "capsule")
                {
                    col.RadiusRaw = def.start_radius;
                    col.RadiusTailScaleRaw = def.start_radius > 0f ? def.end_radius / def.start_radius : 1f;
                    col.HeightRaw = def.length;
                }
                else
                {
                    col.RadiusRaw = def.radius;
                    col.RadiusTailScaleRaw = 1f;
                    col.HeightRaw = 0f;   // 0 なら球として扱われる
                }
                madeColliders[def.name] = col;
                summary.Add($"コライダ: {def.name} -> 親 {bone.name} ({def.shape})");
                Debug.Log($"[PoC-2] コライダ生成: {def.name} -> 親 {bone.name} ({def.shape})");
            }

            // --- チェーンを作る ---
            foreach (var chain in data.chains)
            {
                if (chainFilter != null && !chain.name.Contains(chainFilter)) continue;

                var rootPoints = new List<SPCRJointDynamicsPoint>();
                foreach (var r in chain.roots)
                {
                    if (r.kind != "fbx_transform" || !boneByFileId.TryGetValue(r.source_file_id, out var bone))
                    {
                        Debug.LogWarning($"[PoC-2] ルートボーンを解決できません: {chain.name} (fileID {r.source_file_id})");
                        continue;
                    }
                    // 根とその配下すべてに Point を付ける。SPCR は Point の無い所で打ち切る
                    var rootPt = AddPointsRecursive(bone, isRoot: true);
                    if (rootPt != null) rootPoints.Add(rootPt);
                }

                if (rootPoints.Count == 0)
                {
                    Debug.LogError($"[PoC-2] {chain.name}: ルートを 1 つも解決できませんでした。");
                    continue;
                }

                // Controller はモデルのルートへ付ける。作者のサンプルでは 7 本すべてが
                // モデルルート(01_kohaku_B)に付いており、これが想定の配置である。
                // コライダ側の OnDrawGizmos が GetComponentsInParent で Controller を
                // 探すため、ここを外すとギズモが「未登録(赤)」になり調査しづらくなる。
                var ctrl = Undo.AddComponent<SPCRJointDynamicsController>(root);
                ctrl.name = chain.name;
                ctrl._RootTransform = root.transform;

                // 物理パラメータはサンプル(kohaku)の値を出発点にする。
                // 新規 AddComponent の既定は全て 1.0 で、布として硬すぎる。
                // 重力は v1 の値を尊重する。Chifuyu のスカートは -4 であり、
                // 既定の -9.8 や kohaku の -10 より弱い。作者の調整である
                var gDir = chain.@params?.gravity_direction?.V ?? Vector3.up;
                var gVal = chain.@params?.gravity?.startValue ?? -10f;
                ctrl._Gravity = gDir.sqrMagnitude > 0f ? gDir.normalized * gVal : new Vector3(0f, gVal, 0f);
                ctrl._StructuralShrinkVertical = 1.0f;
                ctrl._StructuralStretchVertical = 0.1f;
                ctrl._StructuralShrinkHorizontal = 1.0f;
                ctrl._StructuralStretchHorizontal = 1.0f;
                ctrl._BendingShrinkVertical = 0.1f;
                ctrl._BendingShrinkHorizontal = 0.1f;
                ctrl._RootPointTbl = rootPoints.ToArray();
                ctrl._ColliderTbl = chain.colliders
                    .Select(c => c.name != null && madeColliders.TryGetValue(c.name, out var m) ? m : null)
                    .Where(m => m != null)
                    .ToArray();

                // チェーンが胴を一周しているか（スカート）を形状から判定する。
                // 名前では決めない。閉じているなら水平方向の拘束を輪にする必要がある
                var isRing = IsClosedRing(rootPoints);
                ctrl._IsLoopRootPoints = isRing;

                // 水平拘束は _RootPointTbl の並び順で張られる。順序を誤ると胴を横断する
                // バネができ、スカートが裂けたり片側が引き込まれたりする
                string sortNote;
                if (isRing)
                {
                    // 輪だと分かっているなら重心まわりの角度で並べるのが確実。
                    // SPCR の SortNearPointXZ は「最も近い点を貪欲に繋ぐ」方式で、
                    // 輪の途中で反対側へ飛ぶことがある。1 本飛ぶだけで片側が崩れる
                    ctrl._RootPointTbl = SortByAngle(rootPoints).ToArray();
                    ctrl.UpdateJointConnection();
                    sortNote = $"角度順 (隣接距離 最小/最大 = {RingUniformity(ctrl._RootPointTbl):F2})";
                }
                else
                {
                    // 輪でないもの（髪の房など）は SPCR の近傍探索に任せる
                    ctrl.SortConstraintsHorizontalRoot(
                        SPCRJointDynamicsController.UpdateJointConnectionType.SortNearPointXZ);
                    sortNote = "近傍探索XZ";
                }

                // ここで _Depth と MaxPointDepth が確定するので、v1 のカーブを転記する。
                // _PointRadius は SPCR の押し出し計算 PushoutFromSphere() が使う「布の厚み」であり、
                // 0 のままだと衣装が体を貫通する。v1 は radius 0.017 -> 0.035 を持っていた
                var rad = chain.@params?.radius;
                var mass = chain.@params?.mass;
                float maxR = 0f;
                if ((rad != null || mass != null) && ctrl.PointTbl != null && ctrl.MaxPointDepth > 0)
                {
                    foreach (var pt in ctrl.PointTbl)
                    {
                        if (pt == null) continue;
                        var rate = Mathf.Clamp01(pt._Depth / ctrl.MaxPointDepth);
                        if (rad != null) { pt._PointRadius = rad.At(rate); maxR = Mathf.Max(maxR, pt._PointRadius); }
                        if (mass != null) pt._Mass = mass.At(rate);
                        EditorUtility.SetDirty(pt);
                    }
                }

                ctrl.UpdateJointDistance();

                var line = $"{chain.name}: ルート {rootPoints.Count} 本 / " +
                           $"Point {ctrl.PointTbl?.Length ?? 0} 個 / コライダ {ctrl._ColliderTbl.Length} 個 / " +
                           $"粒子半径 最大 {maxR:F3} / 重力 {ctrl._Gravity.y:F1} / " +
                           $"輪 {(ctrl._IsLoopRootPoints ? "はい" : "いいえ")} / 並べ替え {sortNote}";
                summary.Add(line);
                Debug.Log("[PoC-2] " + line);
            }

            EditorUtility.SetDirty(root);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);

            var report = string.Join("\n", summary);
            Debug.Log("[PoC-2] 完了\n" + report);
            EditorUtility.DisplayDialog("PoC-2 変換結果",
                report + "\n\nルートが 0 本なら fileID の突き合わせが失敗しています。", "OK");
        }

        /// 根が胴を一周しているか（＝スカートのような閉じた輪か）を形状から判定する。
        /// XZ 平面上で重心まわりの角度を並べ、最大の隙間が小さければ閉じているとみなす。
        /// 名前で判定しないのは、モデルごとに命名が違うためである。
        static bool IsClosedRing(List<SPCRJointDynamicsPoint> pts)
        {
            if (pts.Count < 4) return false;

            var center = Vector3.zero;
            foreach (var p in pts) center += p.transform.position;
            center /= pts.Count;

            var angles = new List<float>();
            foreach (var p in pts)
            {
                var d = p.transform.position - center;
                angles.Add(Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg);
            }
            angles.Sort();

            float maxGap = 360f + angles[0] - angles[angles.Count - 1];   // 最後から最初へ回り込む隙間
            for (int i = 1; i < angles.Count; ++i)
                maxGap = Mathf.Max(maxGap, angles[i] - angles[i - 1]);

            return maxGap < 90f;
        }

        /// 重心まわりの角度で根を並べる。閉じた輪ならこれが正しい隣接順である。
        static List<SPCRJointDynamicsPoint> SortByAngle(List<SPCRJointDynamicsPoint> pts)
        {
            var center = Vector3.zero;
            foreach (var p in pts) center += p.transform.position;
            center /= pts.Count;

            var sorted = new List<SPCRJointDynamicsPoint>(pts);
            sorted.Sort((a, b) =>
            {
                var da = a.transform.position - center;
                var db = b.transform.position - center;
                return Mathf.Atan2(da.z, da.x).CompareTo(Mathf.Atan2(db.z, db.x));
            });
            return sorted;
        }

        /// 並べたあとの隣接距離の均一さ。1.0 に近いほど素直な輪。
        /// 極端に小さいと、どこかで胴を横断している疑いがある。
        static float RingUniformity(SPCRJointDynamicsPoint[] pts)
        {
            if (pts == null || pts.Length < 3) return 1f;
            float min = float.MaxValue, max = 0f;
            for (int i = 0; i < pts.Length; ++i)
            {
                var a = pts[i].transform.position;
                var b = pts[(i + 1) % pts.Length].transform.position;
                var d = Vector3.Distance(a, b);
                min = Mathf.Min(min, d);
                max = Mathf.Max(max, d);
            }
            return max > 0f ? min / max : 1f;
        }

        /// v1 の axis(0=X,1=Y,2=Z) を SPCR の規約(常に Y)へ合わせる補正回転。
        /// R * (0,1,0) が v1 の軸方向に一致するようにする。
        static Quaternion AxisFix(int axis)
        {
            switch (axis)
            {
                case 0: return Quaternion.Euler(0f, 0f, -90f);  // Y -> X
                case 2: return Quaternion.Euler(90f, 0f, 0f);   // Y -> Z
                default: return Quaternion.identity;            // Y -> Y
            }
        }

        /// シーン上の Transform から FBX 側の localFileId への対応表を作る。
        /// 名前ではなく fileID で引き当てるための土台（要件 F-18-7）。
        static Dictionary<string, Transform> BuildBoneMap(Transform root)
        {
            var map = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var origin = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t);
                if (origin == null) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(origin, out _, out long localId)) continue;
                map[localId.ToString()] = t;   // 同じ id が複数あれば後勝ち。実データでは衝突しない
            }
            return map;
        }

        static SPCRJointDynamicsPoint AddPointsRecursive(Transform t, bool isRoot)
        {
            // 自分たちが生成したものと v1 の残骸は骨として扱わない
            if (t.name.StartsWith("SPCR ") || t.name.StartsWith("Magica ")) return null;

            var pt = t.GetComponent<SPCRJointDynamicsPoint>() ?? Undo.AddComponent<SPCRJointDynamicsPoint>(t.gameObject);
            pt._IsFixed = isRoot;   // 根は腰に固定。動かすのはその先だけ

            for (int i = 0; i < t.childCount; ++i)
            {
                AddPointsRecursive(t.GetChild(i), isRoot: false);
            }
            return pt;
        }
    }
}
