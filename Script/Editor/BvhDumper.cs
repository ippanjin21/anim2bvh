using System.Collections;
using System.Collections.Generic;
using System.IO;
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

        private Dictionary<string, string> _motionStateDictionary = new Dictionary<string, string>();
        private List<Transform> transforms_ = new List<Transform>();
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

            errorDetected_ = false;
            if (animator.isHuman)
            {
                DumpHumanJoint(root, animator, fs);
            } else
            {
                DumpGenericJoin(root, fs);
            }
            if (errorDetected_)
            {
                Debug.LogError("Error detected while dumping transforms.");
                return;
            }

            fs.WriteLine("MOTION");
            int frameCount = Mathf.RoundToInt(clipLength * frameRate);
            fs.WriteLine("Frames: " + frameCount);
            fs.WriteLine("Frame Time: " + (1.0f / frameRate).ToString("F8"));

            if (_dumpRootMotion)
            {
                _baseRootPosition = root.transform.position;
                _baseRootRotation = root.transform.rotation;
            }

            // 現在の Transform を記憶する。
            BackupTransforms(_positionsBackup, _rotationsBackup, _target.transform);
            // Animator の状態を記憶する。
            float speed = animator.speed;
            AnimatorUpdateMode updateMode = animator.updateMode;
            bool applyRootMotion = animator.applyRootMotion;
            AnimatorCullingMode cullingMode = animator.cullingMode;

            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Animation をステップ再生させながら、Motion の情報を抜き出す。
            for (int i = 0; i < frameCount; i++)
            {
                float frameSec = i / frameRate;
                animator.PlayInFixedTime(stateName, 0, frameSec);
                animator.speed = 1f;
                animator.Update(1.0f / frameRate);
                DumpTranforms(transforms_, fs);
            }

            // 元の Transform に戻す。
            RecoverTransforms(_positionsBackup, _rotationsBackup, _target.transform);
            // Animator の状態を戻す。
            animator.speed = speed;
            animator.updateMode = updateMode;
            animator.applyRootMotion = applyRootMotion;
            animator.cullingMode = cullingMode;

            _positionsBackup.Clear();
            _rotationsBackup.Clear();
        }

        private bool errorDetected_ = false;

        private void DumpHumanJoint(in Transform root, in Animator animator, in StreamWriter fs)
        {
            transforms_.Clear();
            TraverseHumanJoint(root, animator, 0, transforms_, fs);
        }

        private bool TraverseHumanJoint(in Transform current, in Animator animator, int level, in List<Transform> transforms, in StreamWriter fs)
        {
            string leadingSpace = GetIndexSpace(level);

            if (IsHumanBodyBone(current, animator))
            {
                if (transforms.Count <= 0)
                {
                    fs.WriteLine(leadingSpace + "HIERARCHY");
                    fs.WriteLine(leadingSpace + "ROOT " + current.name);
                    fs.WriteLine(leadingSpace + "{");
                    fs.WriteLine(leadingSpace + "    OFFSET 0.00000000 0.00000000 0.00000000");
                    // Unity の EulerAngle は Z-X-Y
                    fs.WriteLine(leadingSpace + "    CHANNELS 6 Xposition Yposition Zposition Yrotation Xrotation Zrotation");
                }
                else
                {
                    fs.WriteLine(leadingSpace + "JOINT " + current.name);
                    fs.WriteLine(leadingSpace + "{");
                    fs.WriteLine(leadingSpace + "    OFFSET " + current.localPosition.x.ToString("F8") + " " + current.localPosition.y.ToString("F8") + " " + current.localPosition.z.ToString("F8"));
                    fs.WriteLine(leadingSpace + "    CHANNELS 3 Yrotation Xrotation Zrotation");
                }

                transforms.Add(current);

                Transform leaf = null;
                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);

                    if (!child.gameObject.activeSelf)
                        continue;

                    if (child.name.ToLower() == current.name.ToLower() + "_end")
                    {
                        // Leaf Node
                        leaf = child;
                    }
                }

                if (leaf == null)
                {
                    int count = 0;
                    for (int i = 0; i < current.childCount; i++)
                    {
                        Transform child = current.GetChild(i);
                        if (!child.gameObject.activeSelf)
                            continue;

                        if (!TraverseHumanJoint(child, animator, level + 1, transforms, fs))
                        {
                            if (!errorDetected_)
                            {
                                Debug.LogError("Missing " + current.name + "_end");
                                errorDetected_ = true;
                            }
                            return false;
                        }
                        count++;
                    }
                    if (count <= 0)
                    {
                        return false; // Not terminated.
                    }
                } else
                {
                    Vector3 offset = leaf.localPosition;
                    fs.WriteLine(leadingSpace + "    END SITE");
                    fs.WriteLine(leadingSpace + "    {");
                    fs.WriteLine(leadingSpace + "        OFFSET " + offset.x.ToString("F8") + " " + offset.y.ToString("F8") + " " + offset.z.ToString("F8"));
                    fs.WriteLine(leadingSpace + "    }");

                    // terminated
                }
                fs.WriteLine(leadingSpace + "}");
            } else
            {
                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);
                    if (!child.gameObject.activeSelf)
                        continue;

                    if (!TraverseHumanJoint(child, animator, level, transforms, fs))
                        return false;
                }
            }
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

        private void DumpGenericJoin(in Transform root, in StreamWriter fs)
        {
            transforms_.Clear();
            TraverseGenericJoint(root, 0, transforms_, fs);
        }

        private bool TraverseGenericJoint(in Transform current, int level, in List<Transform> transforms, in StreamWriter fs)
        {
            string leadingSpace = GetIndexSpace(level);

            if (transforms.Count <= 0)
            {
                fs.WriteLine(leadingSpace + "HIERARCHY");
                fs.WriteLine(leadingSpace + "ROOT " + current.name);
                fs.WriteLine(leadingSpace + "{");
                fs.WriteLine(leadingSpace + "    OFFSET 0.00000000 0.00000000 0.00000000");
                // Unity の EulerAngle は Z-X-Y
                fs.WriteLine(leadingSpace + "    CHANNELS 6 Xposition Yposition Zposition Yrotation Xrotation Zrotation");
            }
            else
            {
                fs.WriteLine(leadingSpace + "JOINT " + current.name);
                fs.WriteLine(leadingSpace + "{");
                fs.WriteLine(leadingSpace + "    OFFSET " + current.localPosition.x.ToString("F8") + " " + current.localPosition.y.ToString("F8") + " " + current.localPosition.z.ToString("F8"));
                fs.WriteLine(leadingSpace + "    CHANNELS 3 Yrotation Xrotation Zrotation");
            }

            transforms.Add(current);

            Transform leaf = null;
            for (int i =0; i < current.childCount; i ++)
            {
                Transform child = current.GetChild(i);

                if (!child.gameObject.activeSelf)
                    continue;

                if (child.name.ToLower() == current.name.ToLower() + "_end")
                {
                    // Leaf Node
                    leaf = child;
                }
            }
            if (leaf == null)
            {
                int count = 0;
                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);
                    if (!child.gameObject.activeSelf)
                        continue;

                    if (!TraverseGenericJoint(child, level + 1, transforms, fs))
                    {
                        return false;
                    }
                    count++;
                }
                if (count <= 0)
                {
                    Debug.LogError("Missing " + current.name + "_end transform.");
                    errorDetected_ = true;
                    return false;
                }
            } else
            {
                Vector3 offset = leaf.localPosition;
                string endName = "SITE";
                fs.WriteLine(leadingSpace + "    END " + endName);
                fs.WriteLine(leadingSpace + "    {");
                fs.WriteLine(leadingSpace + "        OFFSET " + offset.x.ToString("F8") + " " + offset.y.ToString("F8") + " " + offset.z.ToString("F8"));
                fs.WriteLine(leadingSpace + "    }");
            }
            fs.WriteLine(leadingSpace + "}");
            return true;
        }

        private void DumpTranforms(in List<Transform> transforms, in StreamWriter fs)
        {
            string buf = "";
            foreach (var item in transforms)
            {
                if (buf.Length == 0)
                {
                    Vector3 rootMovement = Vector3.zero;
                    Vector3 rootEulerRotation = Vector3.zero;

                    if (_dumpRootMotion)
                    {
                        rootMovement = item.transform.position - _baseRootPosition;
                        Quaternion rootRotation = item.transform.rotation * Quaternion.Inverse(_baseRootRotation);

                        rootEulerRotation = NormalizeEulerAngle(rootRotation.eulerAngles);
                    }
                    else
                    {
                        rootMovement = item.localPosition;
                        rootEulerRotation = NormalizeEulerAngle(item.localRotation.eulerAngles);
                    }

                    // Xposition Yposition Zposition Yrotation Xrotation Zrotation
                    buf +=
                        rootMovement.x.ToString("F8") + " " + rootMovement.y.ToString("F8") + " " + rootMovement.z.ToString("F8") + " " +
                        rootEulerRotation.y.ToString("F8") + " " + rootEulerRotation.x.ToString("F8") + " " + rootEulerRotation.z.ToString("F8");
                }
                else
                {
                    Vector3 angle = NormalizeEulerAngle(item.localRotation.eulerAngles);

                    // Yrotation Xrotation Zrotation
                    buf += " " + angle.y.ToString("F8") + " " + angle.x.ToString("F8") + " " + angle.z.ToString("F8");
                }
            }
            fs.WriteLine(buf);
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

        private void RecoverTransforms(in LinkedList<Vector3> positions, in LinkedList<Quaternion> rotations, in Transform root)
        {
            root.localPosition = positions.First.Value;
            root.localRotation = rotations.First.Value;
            positions.RemoveFirst();
            rotations.RemoveFirst();
            for (int i = 0; i < root.childCount; i ++)
            {
                RecoverTransforms(positions, rotations, root.GetChild(i));
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

 

        private static Vector3 NormalizeEulerAngle(in Vector3 angle)
        {
            return new Vector3(
                angle.x > 180.0f ? angle.x - 360.0f : angle.x,
                angle.y > 180.0f ? angle.y - 360.0f : angle.y,
                angle.z > 180.0f ? angle.z - 360.0f : angle.z);
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

