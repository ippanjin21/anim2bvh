using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace ippanjin21
{
    [CustomEditor(typeof(BvhDumpTarget))]
    public class BvhDumper : Editor
    {
        private BvhDumpTarget _target;
        private string[] _motionNames = null;
        private int _selectIndex = 0;
        private bool _dumpRootMotion = false;
        private Transform _selectRootTransform = null;
        private bool _ToRightHandedCoordinate = true;

        private Dictionary<string, string> _motionStateDictionary = new Dictionary<string, string>();
        private LinkedList<Vector3> _positionsBackup = new LinkedList<Vector3>();
        private LinkedList<Quaternion> _rotationsBackup = new LinkedList<Quaternion>();

        private Vector3 _baseRootPosition = Vector3.zero;
        private Quaternion _baseRootRotation = Quaternion.identity;

        private void OnEnable()
        {
            _target = (BvhDumpTarget)target;
        }

        public override void OnInspectorGUI()
        {
            Animator animator = _target.GetComponent<Animator>();
            if (animator == null)
            {
                EditorGUILayout.LabelField("No Animator.");
                return;
            }

            UpdateAnimatorStateNames(animator, 0);
            if (_motionNames == null)
            {
                EditorGUILayout.LabelField("No AnimatorStates.");
                return;
            }

            _selectIndex = EditorGUILayout.Popup("AnimationClip", _selectIndex, _motionNames);
            _dumpRootMotion = EditorGUILayout.Toggle("Dump RootMotion/Rotation", _dumpRootMotion);
            _selectRootTransform = (Transform)EditorGUILayout.ObjectField("Root Transform", _selectRootTransform, typeof(Transform), true);
            _ToRightHandedCoordinate = EditorGUILayout.Toggle("To Right-handed coordinate", _ToRightHandedCoordinate);

            // Bone の構造を Dump するだけに留めるのが良いようだ。
            if (GUILayout.Button("Dump"))
            {
                if (_selectIndex < 0 || _selectIndex >= _motionNames.Length)
                {
                    Debug.LogError("Bad motion.");
                    return;
                }
                string motionName = _motionNames[_selectIndex];
                if (!_motionStateDictionary.TryGetValue(motionName, out string stateName))
                {
                    Debug.LogError("No AnimatorState whose motion is " + motionName);
                    return;
                }

                string outputName = EditorUtility.SaveFilePanel("Save BVH", "", motionName + ".bvh", "bvh");
                if (outputName.Length <= 0)
                {
                    return;
                }
                using (var fs = new System.IO.StreamWriter(outputName, false))
                {
                    Dump(animator, motionName, stateName, fs);
                }
            }
        }

        private void UpdateAnimatorStateNames(in Animator animator, in int layerIndex)
        {
            _motionStateDictionary.Clear();
            _motionNames = null;

            UnityEditor.Animations.AnimatorController animationController = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
            if (animationController == null || animationController.layers == null || animationController.layers.Length <= 0)
                return;

            var rootStateMachine = animationController.layers[0].stateMachine;
            if (rootStateMachine.states.Length <= 0)
            {
                return;
            }

            foreach (var state in rootStateMachine.states)
            {
                if (state.state.motion != null)
                {
                    _motionStateDictionary[state.state.motion.name] = state.state.name;
                }
            }
            if (_motionStateDictionary.Keys.Count <= 0)
            {
                return;
            }

            string[] motionNames = new string[_motionStateDictionary.Keys.Count];
            int index = 0;
            foreach (var state in rootStateMachine.states)
            {
                if (state.state.motion != null)
                {
                    motionNames[index] = state.state.motion.name;
                    index++;
                }
            }
            _motionNames = motionNames;
        }

        private bool GetAnimationClipFrameRateLength(Animator animator, in string name, out float clipLength, out float frameRate)
        {
            UnityEditor.Animations.AnimatorController animationController = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
            if (animationController != null)
            {
                for (int i = 0; i < animationController.animationClips.Length; i++)
                {
                    AnimationClip clip = animationController.animationClips[i];
                    if (clip.name == name)
                    {
                        clipLength = clip.length;
                        frameRate = clip.frameRate;
                        return true;
                    }
                }
            }
            clipLength = 0.0f;
            frameRate = 0.0f;
            return false;
        }

        private void Dump(in Animator animator, in string motionName, in string stateName, in StreamWriter fs)
        {
            if (!GetAnimationClipFrameRateLength(animator, motionName, out float clipLength, out float frameRate))
            {
                Debug.LogError("No AnimationClip named " + motionName);
                return;
            }

            // BVH のために、Bone 情報を抜き出す。
            Transform root = _selectRootTransform;
            if (root == null)
            {
                root = _target.transform.Find("Armature");
                if (root == null)
                {
                    Debug.LogError("No default root node: Armature.");
                    return;
                }
            } else
            {
                if (!IsDescendantOf(root, _target.transform))
                {
                    Debug.LogError("RootTransform " + _selectRootTransform + " is not descendant of " + _target.transform);
                    return;
                }
            }

            _baseRootPosition = root.transform.position;
            _baseRootRotation = root.transform.rotation;

            Bone rootBone = null;
            if (animator.isHuman)
            {
                //DumpHumanJoint(root, animator, fs);
                rootBone = GenerateHumanJoint(root, animator);
                if (rootBone == null)
                {
                    return;
                }
            }
            else
            {
                return;
            }

            DumpHumanJoint(rootBone, fs);


            fs.WriteLine("MOTION");
            int frameCount = Mathf.RoundToInt(clipLength * frameRate);
            fs.WriteLine("Frames: " + frameCount);
            fs.WriteLine("Frame Time: " + (1.0f / frameRate).ToString("F8"));

            // 現在の Transform を記憶する。
            BackupTransforms(_positionsBackup, _rotationsBackup, _target.transform);
            // Animator の状態を記憶する。
            float speed = animator.speed;
            AnimatorUpdateMode updateMode = animator.updateMode;
            bool applyRootMotion = animator.applyRootMotion;
            AnimatorCullingMode cullingMode = animator.cullingMode;

            animator.updateMode = AnimatorUpdateMode.UnscaledTime;
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            animator.speed = 1f;
            for (int i = 0; i < frameCount; i++)
            {
                float frameSec = i / frameRate;
                animator.PlayInFixedTime(stateName, 0, frameSec);
                animator.Update(0.0000001f);
                //Debug.Log("NormalizedTime = " + animator.GetCurrentAnimatorStateInfo(0).normalizedTime.ToString("F8"));
                DumpHumanTransform(rootBone, fs);
            }

            // 元の Transform に戻す。
            RestoreTransforms(_positionsBackup, _rotationsBackup, _target.transform);
            // Animator の状態を戻す。
            animator.speed = speed;
            animator.updateMode = updateMode;
            animator.applyRootMotion = applyRootMotion;
            animator.cullingMode = cullingMode;

            _positionsBackup.Clear();
            _rotationsBackup.Clear();
        }

        class Bone
        {
            private enum Termination
            {
                NotTerminated,
                Terminated,
                Root,
            };

            private Transform transform_ = null;
            private Vector3 basePosition_ = Vector3.zero;
            private Quaternion baseRotation_ = Quaternion.identity;
            private Termination terminated_ = Termination.NotTerminated;
            private Bone parent_ = null;
            private LinkedList<Bone> children_ = null;


            public static Bone Create(in Transform transform)
            {
                return new Bone(transform);
            }

            public static Bone CreateTerminated(in Transform transform)
            {
                return new Bone(transform, Termination.Terminated);
            }

            public bool MarkRoot()
            {
                if (IsTerminated)
                    return false;
                terminated_ = Termination.Root;
                return true;
            }

            public Transform Transform { get { return transform_; } }

            public bool IsRoot { get { return parent_ == null || terminated_ == Termination.Root; } }

            public bool IsTerminated { get { return terminated_ == Termination.Terminated; } }

            public Bone Parent { get { return parent_; } }

            public Vector3 BasePosition { get { return basePosition_; } }
            public Quaternion BaseRotation { get { return baseRotation_; } }

            public bool AddChild(in Bone child)
            {
                if (IsTerminated || children_ == null)
                    return false;
                if (child.IsTerminated && children_.Count > 0)
                    return false;
                if (child.terminated_ == Termination.Root)
                    return false;
                children_.AddLast(child);
                child.parent_ = this;
                return true;
            }

            public bool RemoveChild(in Bone child)
            {
                if (children_ == null)
                    return false;
                if (!children_.Remove(child))
                    return false;
                child.parent_ = null;
                return true;
            }

            public int ChildCount { get { return children_ == null ? 0 : children_.Count; } }

            public LinkedList<Bone> Children { get { return children_; } }


            public Quaternion LocalRotation
            {
                get
                {
                    if (parent_ == null)
                    {
                        return Quaternion.Inverse(baseRotation_) * Transform.rotation;
                    }

                    //if (parent_.Transform == Transform.parent)
                   // {
                    //    return Transform.localRotation * Quaternion.Inverse(baseRotation_);
                   // }
                    //else
                    {
                        return Quaternion.Inverse(baseRotation_) * Quaternion.Inverse(parent_.Transform.rotation) * Transform.rotation;
                    }
                }
            }

            public void UpdateBasePositionRotation()
            {
                if (parent_ == null)
                {
                    baseRotation_ = Transform.rotation;
                    basePosition_ = Transform.position;
                    return;
                }
                //if (parent_.Transform == Transform.parent)
                //{
                //    baseRotation_ = Transform.localRotation;
                //    basePosition_ = Transform.localPosition;
                //}
                //else
                {
                    // Quaternion の演算順序は正しいのか？ Quaternion の積は交換法則が成立しないので、
                    // 順序が重要。

                    baseRotation_ = Quaternion.Inverse(parent_.Transform.rotation) * Transform.rotation;
                    basePosition_ = Quaternion.Inverse(baseRotation_) * (Transform.position - parent_.Transform.position);

                    Debug.AssertFormat(Quaternion.Angle(baseRotation_, Transform.localRotation) < 1.0f,
                        baseRotation_.ToString("F8") + " != "  + Transform.localRotation.ToString("F8")
                        );
                }
            }


            private Bone(in Transform transform)
            {
                transform_ = transform;
                terminated_ = Termination.NotTerminated;
                children_ = new LinkedList<Bone>();
            }

            private Bone(in Transform transform, in Termination terminated)
            {
                transform_ = transform;
                terminated_ = terminated;
                if (terminated_ != Termination.Terminated)
                {
                    children_ = new LinkedList<Bone>();
                }
                else
                {
                    children_ = null;
                }
            }
        };

        private Bone GenerateHumanJoint(in Transform root, in Animator animator)
        {
            Bone rootBone = null;
            bool rootIsHumanBodyBone = false;
            if (IsHumanBodyBone(root, animator))
            {
                rootBone = Bone.Create(root.parent);
                rootIsHumanBodyBone = true;
            }
            else
            {
                rootBone = Bone.Create(root);
            }
            if (!TraverseHumanJoint(rootBone, root, animator))
            {
                return null;
            }

            if (rootBone.ChildCount != 1)
            {
                return null;
            }

            UpdateBasePositionRotation(rootBone);

            // RootMotion を生成し、かつ Root が HumanBodyBone ではない(例. Armature)なら
            // Root をそのまま使う。
            if (_dumpRootMotion && !rootIsHumanBodyBone)
            {
                return rootBone;
            }

            // Root はその子供。(RootBone は Parent Transform が欲しいのでそのためだけに残す)
            Bone bone = rootBone.Children.First.Value;
            if (!bone.MarkRoot())
            {
                return null;
            }
            return bone;
        }

        private void UpdateBasePositionRotation(in Bone current)
        {
            current.UpdateBasePositionRotation();
            if (current.ChildCount <= 0)
                return;

            foreach(var child in current.Children)
            {
                UpdateBasePositionRotation(child);
            }
        }

        private bool TraverseHumanJoint(in Bone parentBone, in Transform current, in Animator animator)
        {
            if (IsHumanBodyBone(current, animator))
            {
                Bone bone = Bone.Create(current);

                if (!parentBone.AddChild(bone))
                    return false;

                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);
                    if (!child.gameObject.activeSelf)
                        continue;

                    if (!TraverseHumanJoint(bone, child, animator))
                        return false;
                }
            } else
            {
                // 終端ノード
                if (parentBone.Transform != null && current.name.ToLower() == parentBone.Transform.name.ToLower() + "_end")
                {
                    return parentBone.AddChild(Bone.CreateTerminated(current));
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);
                    if (!child.gameObject.activeSelf)
                        continue;

                    if (!TraverseHumanJoint(parentBone, child, animator))
                        return false;
                }
            }
            return true;
        }

        private void DumpHumanJoint(in Bone root, in StreamWriter fs)
        {
            DumpHumanJoint(root, 0, fs);
        }

        private bool DumpHumanJoint(in Bone current, int level, in StreamWriter fs)
        {
            string leadingSpace = GetIndexSpace(level);

            if (current.IsRoot)
            {
                fs.WriteLine(leadingSpace + "HIERARCHY");
                fs.WriteLine(leadingSpace + "ROOT " + current.Transform.name);
                fs.WriteLine(leadingSpace + "{");

                //fs.WriteLine(leadingSpace + "    OFFSET 0.0000000 0.0000000 0.0000000");
                fs.WriteLine(leadingSpace + "    OFFSET " + PositionToString(current.Transform.position - (current.Parent != null ? current.Parent.Transform.position : current.BasePosition)));

                // Unity の EulerAngle は Z-X-Y
                fs.WriteLine(leadingSpace + "    CHANNELS 6 Xposition Yposition Zposition Yrotation Xrotation Zrotation");
            }
            else
            {
                Vector3 localPos = CovertPosition(current.Transform.position - current.Parent.Transform.position);
                if (!current.IsTerminated)
                {
                    fs.WriteLine(leadingSpace + "JOINT " + current.Transform.name);
                    fs.WriteLine(leadingSpace + "{");
                    // Blender は JOINT から勝手に Reset Matrix を作り出して、渡した Rotation をずらしてしまう。従って、JOINT は常に余計な Reset Matrix が
                    // 生じない 0 Length 0 に固定する。
                    fs.WriteLine(leadingSpace + "    OFFSET 0.000000 " + localPos.magnitude.ToString("F8") + " 0.0000000");
                    fs.WriteLine(leadingSpace + "    CHANNELS 3 Yrotation Xrotation Zrotation");
                } else
                {
                    Vector3 endOffset = CovertPosition(current.Transform.position - current.Parent.Transform.position);
                    fs.WriteLine(leadingSpace + "    END SITE");
                    fs.WriteLine(leadingSpace + "    {");
                    fs.WriteLine(leadingSpace + "        OFFSET 0.000000 " + endOffset.magnitude.ToString("F8") + " 0.0000000");
                    fs.WriteLine(leadingSpace + "    }");
                    return true;
                }
            }

            if (!current.IsTerminated && current.ChildCount <= 0)
            {
                // Error
                Debug.LogError("Bone: \"" + current.Transform.name + "\" is not terminated by \"" + current.Transform.name + "_end\".");
                return false;
            }

            foreach(var child in current.Children)
            {
                if (!DumpHumanJoint(child, level + 1, fs))
                {
                    return false;
                }
            }

            fs.WriteLine(leadingSpace + "}");
            return true;
        }



        private bool IsHumanBodyBone(in Transform current, in Animator animator)
        {
            for (HumanBodyBones boneId = HumanBodyBones.Hips; boneId < HumanBodyBones.LastBone; boneId++)
            {
                if (current == animator.GetBoneTransform(boneId))
                {
                    return true;
                }
            }
            if (_dumpRootMotion && current.IsChildOf(_target.transform) && current.name == "Armature")
            {
                return true;
            }
            return false;
        }


        private void DumpHumanTransform(in Bone root, in StreamWriter fs)
        {
            StringBuilder builder = new StringBuilder();
            DumpHumanTransform(root, Quaternion.identity, builder);
            fs.WriteLine(builder.ToString());
        }

        private void DumpHumanTransform(in Bone current, in Quaternion parentRotation, in StringBuilder builder)
        {
            Quaternion currentRotation;

            if (current.IsRoot)
            {
                Vector3 currentPosition;

                if (_dumpRootMotion)
                {
                    currentPosition = current.Transform.position - _baseRootPosition;
                    currentRotation = current.Transform.rotation * Quaternion.Inverse(_baseRootRotation);
                }
                else
                {
                    currentPosition = current.Transform.position - (current.Parent != null ? current.Parent.Transform.position : current.BasePosition);
                    currentRotation = current.LocalRotation;
                }
                builder.Append(PositionToString(CovertPosition(currentPosition)) + " " + RotationToString(ConvertRotation(currentRotation)));
            }
            else
            {
                currentRotation = current.LocalRotation;

                if (current.ChildCount == 0)
                {
                    // Terminate される。Terminate の Rotation は不要。
                    Debug.Assert(current.IsTerminated);
                    return;
                }
#if false
                if (current.Transform.name.ToLower() == "hand_r")
                {
                    Debug.Log(current.Transform.name + " : localRotation=" + NormalizeEulerAngle(current.Transform.localRotation.eulerAngles).ToString("F8"));
                    Debug.Log(current.Transform.name + " : currentRotation=" + NormalizeEulerAngle(currentRotation.eulerAngles).ToString("F8") + ", " + currentRotation.ToString("F8"));
                    //Debug.Log(current.Transform.name + " : " + NormalizeEulerAngle(ToEulerXYZ(currentRotation)).ToString("F8") + ", " + currentRotation.ToString("F8"));
                }
#endif
                builder.Append(" " + RotationToString(ConvertRotation(currentRotation)));
            }
            foreach(var child in current.Children)
            {
                DumpHumanTransform(child, currentRotation, builder);
            }
        }

        private void BackupTransforms(in LinkedList<Vector3> positions, in LinkedList<Quaternion> rotations, in Transform root)
        {
            if (root == null)
                return;

            positions.AddLast(root.localPosition);
            rotations.AddLast(root.localRotation);
            for (int i = 0; i < root.childCount; i++)
            {
                BackupTransforms(positions, rotations, root.GetChild(i));
            }
        }

        private void RestoreTransforms(in LinkedList<Vector3> positions, in LinkedList<Quaternion> rotations, in Transform root)
        {
            root.localPosition = positions.First.Value;
            root.localRotation = rotations.First.Value;
            positions.RemoveFirst();
            rotations.RemoveFirst();
            for (int i = 0; i < root.childCount; i ++)
            {
                RestoreTransforms(positions, rotations, root.GetChild(i));
            }
        }

        private string GetIndexSpace(in int level)
        {
            string buf = "";
            for (int i = 0; i < level; i++)
            {
                buf += "    ";
            }
            return buf;
        }

        private Vector3 CovertPosition(in Vector3 position)
        {
            return _ToRightHandedCoordinate? new Vector3(-position.x, position.y, position.z) : position;
        }

        private Quaternion ConvertRotation(in Quaternion rotation)
        {
            return _ToRightHandedCoordinate ? Quaternion.Inverse(new Quaternion(-rotation.x, rotation.y, rotation.z, rotation.w)) : rotation;
        }

        private string PositionToString(in Vector3 position)
        {
            return position.x.ToString("F8") + " " + position.y.ToString("F8") + " " + position.z.ToString("F8");
        }

        private string RotationToString(in Quaternion rotation)
        {
            Vector3 eulerAngles = rotation.eulerAngles;
            return eulerAngles.y.ToString("F8") + " " + eulerAngles.x.ToString("F8") + " " + eulerAngles.z.ToString("F8");
        }

        private static Vector3 ToEulerXYZ(in Quaternion rotation)
        {
            float sy = -(2.0f * rotation.x * rotation.z - 2.0f * rotation.y * rotation.w);
            bool unlocked = Mathf.Abs(sy) < 0.99999999f;
            float x, y, z;

            if (unlocked)
            {
                x = Mathf.Atan2(2.0f * rotation.y * rotation.z + 2.0f * rotation.x * rotation.w, 2.0f * rotation.w * rotation.w + 2.0f * rotation.z * rotation.z - 1.0f);
                z = Mathf.Atan2(2.0f * rotation.x * rotation.y + 2.0f * rotation.z * rotation.w, 2.0f * rotation.w * rotation.w + 2.0f * rotation.x * rotation.x - 1.0f);
            }
            else
            {
                x = 0.0f;
                z = Mathf.Atan2(-(2.0f * rotation.x * rotation.y - 2.0f * rotation.z * rotation.w), 2.0f * rotation.w * rotation.w + 2.0f * rotation.y * rotation.y - 1.0f);
            }
            y = Mathf.Asin(sy);
            return new Vector3(x * 180.0f / Mathf.PI, y * 180.0f / Mathf.PI, z * 180.0f / Mathf.PI);
        }

        private static Vector3 NormalizeEulerAngle(in Vector3 angle)
        {
            // 360 を 0 にするのは間違いか？ Normalize の問題はどう解く？
            return new Vector3(angle.x > 180.0f ? (angle.x - 360.0f) : angle.x,
                angle.y > 180.0f ? (angle.y - 360.0f) : angle.y,
                angle.z > 180.0f ? (angle.z - 360.0f) : angle.z);
        }

        private bool IsDescendantOf(in Transform target, in Transform ancestor)
        {
            for (Transform t = target; t != null; t = t.parent)
            {
                if (t == ancestor)
                    return true;
            }
            return false;
        }
    }
}

