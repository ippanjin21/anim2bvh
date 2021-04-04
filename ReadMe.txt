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

(TODO: support generic models)

(2) Setup AnimationController.
(Create a state and add a motion to be dumped to the state.)

(3) Select AnimationClip (Bvh Dump Target(Script)) and press "Dump".

---
end nodes:
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


