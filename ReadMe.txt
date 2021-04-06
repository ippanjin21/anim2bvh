[Install]
- copy BvhDumpTarget.cs into Unity asset folder.
- create "Editor" folder under Unity asset folder and copy BvhDumper.cs into the folder.

----
[How to use]

(1) Add Component: BvhDumpTarget to a model.

E.g.
  Model
  - Animator
  +-----------Armature
                 |
                 +-----hip
                        |
                        +----spine 
...


(2) Setup AnimationController.
(Create a state and add a motion to be dumped to the state.)

(3) Select AnimationClip (Bvh Dump Target(Script)) and press "Dump".

(4) Import the generated BVH by blender...

If root motion / rotation is needed, check "Dump RootMotion/Rotation". Root motion and rotation will be applied to the specified root node.
(If not checked, only human body bones will be dumped as "JOINT". If checked, "Armature" bone will be created as "ROOT JOINT" and root motion and rotation will be applied to the armature bone.)
---
[Leaf nodes]
...
   --- head
         |
         +--- head_end <== required
(bvh's requirement)

If no _end nodes, create a game object (e.g. under head) and setup its position.

E.g.
   --- head
         |
         +--- hair1
         |
         +--- hair2

==>
   --- head
         |
         +--- hair1
         |
         +--- hair2
         |
         +--- head_end (new!)


