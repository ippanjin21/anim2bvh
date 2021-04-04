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
        private Dictionary<string, string> _motionStateDictionary = new Dictionary<string, string>();
        private List<Transform> transforms_ = new List<Transform>();

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
                    DumpHuman(_target.GetComponent<Animator>(), motionName, stateName, false, fs);
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

        private void DumpHuman(in Animator animator, in string motionName, in string stateName, in bool dumpArmature, in StreamWriter fs)
        {
            if (!animator.isHuman)
            {
                return;
            }

            UnityEditor.Animations.AnimatorController animationController = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
            if (animationController == null)
                return;

            float clipLength = 0.0f;
            float frameRate = 0.0f;
            bool found = false;
            for (int i = 0; i < animationController.animationClips.Length; i++)
            {
                AnimationClip clip = animationController.animationClips[i];
                if (clip.name == motionName)
                {
                    clipLength = clip.length;
                    frameRate = clip.frameRate;
                    found = true;
                }
            }
            if (!found)
            {
                Debug.LogError("No AnimationClip named " + motionName);
                return;
            }

            // BVH のために、Bone 情報を抜き出す。
            Transform armature = _target.transform.Find("Armature");
            DumpHumanJoint(armature, animator, fs);

            fs.WriteLine("MOTION");
            int frameCount = Mathf.RoundToInt(clipLength * frameRate);
            fs.WriteLine("Frames: " + frameCount);
            fs.WriteLine("Frame Time: " + (1.0f / frameRate).ToString("F8"));

            // Animation をステップ再生させながら、Motion の情報を抜き出す。
            for (int i = 0; i < frameCount; i++)
            {
                float frameSec = i / frameRate;
                animator.PlayInFixedTime(stateName, 0, frameSec);
                animator.speed = 1f;
                animator.Update(1.0f / frameRate);
                //Debug.Log(frameSec + " : " + animator.GetBoneTransform(HumanBodyBones.Hips).localEulerAngles.ToString("F8"));
                DumpHumanTranforms(transforms_, fs);
            }
        }

        private void DumpHumanJoint(in Transform root, in Animator animator, in StreamWriter fs)
        {
            transforms_.Clear();
            TraverseHumanJoint(root, animator, 0, transforms_, fs);
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

        private bool TraverseHumanJoint(in Transform current, in Animator animator, int level, in List<Transform> transforms, in StreamWriter fs)
        {
            bool isLeafNode = true;
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
                for (int i = 0; i < current.childCount; i++)
                {
                    isLeafNode = TraverseHumanJoint(current.GetChild(i), animator, level + 1, transforms, fs) && isLeafNode;
                }

                if (isLeafNode)
                {
                    Vector3 offset = Vector3.zero;
                    string endName = "SITE";
                    // 骨格構造の終端となるノードを定義する必要がある。
                    // _end という名前の終端ノードが定義されていると仮定する。
                    for (int i = 0; i < current.childCount; i++)
                    {
                        Transform child = current.GetChild(i);
                        if (child.name.ToLower() == current.name.ToLower() + "_end")
                        {
                            offset = child.localPosition;
                            //endName = child.name;
                        }
                    }
                    if (endName == null)
                    {
                        Debug.LogError("Missing end node of " + current.name + ". Please add a new game object named: " + current.name + "_end with valid position under " + current.name);
                    }
                    fs.WriteLine(leadingSpace + "    END " + endName);
                    fs.WriteLine(leadingSpace + "    {");
                    fs.WriteLine(leadingSpace + "        OFFSET " + offset.x.ToString("F8") + " " + offset.y.ToString("F8") + " " + offset.z.ToString("F8"));
                    fs.WriteLine(leadingSpace + "    }");
                }
                fs.WriteLine(leadingSpace + "}");

                // このノードが存在したので Leaf ではない。
                return false;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                isLeafNode = TraverseHumanJoint(current.GetChild(i), animator, level, transforms, fs) && isLeafNode;
            }
            return isLeafNode;
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
            return false;
        }

        private void DumpHumanTranforms(in List<Transform> transforms, in StreamWriter fs)
        {
            string buf = "";
            foreach (var item in transforms)
            {
                Vector3 angle = NormalizeEulerAngle(item.localRotation.eulerAngles);
                if (buf.Length == 0)
                {
                    // Xposition Yposition Zposition Yrotation Xrotation Zrotation
                    buf +=
                        item.localPosition.x.ToString("F8") + " " + item.localPosition.y.ToString("F8") + " " + item.localPosition.z.ToString("F8") + " " +
                        angle.y.ToString("F8") + " " + angle.x.ToString("F8") + " " + angle.z.ToString("F8");
                }
                else
                {
                    // Yrotation Xrotation Zrotation
                    buf += " " + angle.y.ToString("F8") + " " + angle.x.ToString("F8") + " " + angle.z.ToString("F8");
                }
            }
            fs.WriteLine(buf);
        }

        private static Vector3 NormalizeEulerAngle(in Vector3 angle)
        {
            return new Vector3(
                angle.x > 180.0f ? angle.x - 360.0f : angle.x,
                angle.y > 180.0f ? angle.y - 360.0f : angle.y,
                angle.z > 180.0f ? angle.z - 360.0f : angle.z);
        }
    }
}

