using System;
using UnityEngine;

namespace NuclearReplay.Playback
{
    // Viewer free camera: WASD move, Q/E down/up, Shift faster, mouse wheel changes the base speed, Ctrl + wheel
    // changes the focal length (mm), Alt + wheel the focus distance, and the mouse turns the view while the right
    // button is held. The lens lives in the lens panel's settings (Lens, shared with the plugin), so the panel, the
    // wheel and Camera Studio keys all set the same thing.
    //
    // Proton note (same cause as the EditorMouseLook fix): with an unlocked cursor Wine delivers only
    // absolute pointer motion, so mouse deltas read zero. The cursor is therefore locked and hidden while
    // the right button is held (relative motion then arrives), and when the deltas still read zero the
    // look falls back to the change in absolute cursor position.
    //
    // A camera state of the game's own CameraStateManager, like CineCamera, so floating origin, audio,
    // LOD and rendering stay the game's. Moves on unscaled time, so it flies while the replay is paused.
    internal sealed class FreeFlyCamera : CameraBaseState
    {
        public float BaseSpeed = 60f;          // m/s
        public float Yaw, Pitch;
        public Vector3 Position;               // global (floating-origin safe)
        public float Fov = -1f;                // lens in degrees; -1 = the game's own until a focal length is set
        public LensSettings Lens = new LensSettings();   // FocalMm 0 = the game's own field of view
        public double LiveFocusM = 50;         // focus distance on the target now (Alt + wheel starts from it)
        public Action LensChanged;             // the wheel changed Lens (the plugin saves it)
        public bool Looking { get; private set; }
        // Set before re-entering to keep the last pose (free camera restored after a backward jump).
        public bool KeepPose;
        // Test hook input (viewer commands): added to the keyboard input for one frame.
        public Vector3 ScriptMove;
        public Vector2 ScriptLook;

        private Vector3 lastMouse;
        private bool haveLastMouse;
        private float savedFov = -1;
        private readonly LensFov lensFov = new LensFov(null, "Free camera");

        public override void EnterState(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(null);
            cam.transform.SetParent(null, worldPositionStays: true);
            CameraStateManager.cameraMode = CameraMode.tv;
            cam.cockpitCamRender.enabled = false;
            cam.mainCamera.nearClipPlane = 1f;
            FlightHud.EnableCanvas(enable: false);
            if (savedFov < 0) savedFov = cam.desiredFOV;
            if (Lens.FocalMm > 0) ApplyFov(cam, (float)LensMath.FovFromFocal(Lens.FocalMm, Lens.SensorInfo, cam.mainCamera.aspect));
            if (KeepPose) KeepPose = false;
            else
            {
                Position = cam.transform.position.ToGlobalPosition().AsVector3();
                var e = cam.transform.rotation.eulerAngles;
                Yaw = e.y;
                Pitch = e.x > 180f ? e.x - 360f : e.x;
            }
        }

        public override void LeaveState(CameraStateManager cam)
        {
            StopLook();
            lensFov.Reset(cam.mainCamera);
            if (savedFov > 0) { cam.mainCamera.fieldOfView = savedFov; cam.desiredFOV = savedFov; }
            savedFov = -1;
        }

        public override void UpdateState(CameraStateManager cam)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool typing = UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject != null
                && UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject.GetComponent<TMPro.TMP_InputField>() != null
                || ViewerUi.KeyboardTaken;   // the viewer's unit list takes typing

            // Mouse look while the right button is held.
            if (Input.GetMouseButton(1) && !ViewerUi.PointerOverViewerUi)
            {
                if (!Looking) StartLook();
                float dx = 0f, dy = 0f;
                try { dx = Input.GetAxisRaw("Mouse X"); dy = Input.GetAxisRaw("Mouse Y"); } catch (Exception) { }
                if (Mathf.Abs(dx) < 1e-5f && Mathf.Abs(dy) < 1e-5f)
                {
                    var mouse = Input.mousePosition;
                    if (haveLastMouse) { dx = (mouse.x - lastMouse.x) * 0.1f; dy = (mouse.y - lastMouse.y) * 0.1f; }
                    lastMouse = mouse; haveLastMouse = true;
                }
                Yaw += dx * 2.2f;
                Pitch -= dy * 2.2f;
            }
            else if (Looking) StopLook();
            Yaw += ScriptLook.x; Pitch -= ScriptLook.y;
            ScriptLook = Vector2.zero;
            Pitch = Mathf.Clamp(Pitch, -89f, 89f);

            float wheel = Input.mouseScrollDelta.y;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (Mathf.Abs(wheel) > 0.01f && ctrl && !ViewerUi.PointerOverViewerUi)
            {
                // Focal length: 10% longer per notch up.
                double mm = Lens.FocalMm > 0 ? Lens.FocalMm : LensMath.FocalFromFov(CurrentFov(cam), Lens.SensorInfo, cam.mainCamera.aspect);
                Lens.FocalMm = Math.Round(LensMath.Clamp(mm * Math.Pow(1.1, wheel), LensMath.MinFocal, LensMath.MaxFocal), 1);
                LensChanged?.Invoke();
            }
            else if (Mathf.Abs(wheel) > 0.01f && alt && !ViewerUi.PointerOverViewerUi)
            {
                // Focus distance: 15% farther per notch up, fixed focus from then on, depth of field on.
                double from = Lens.FocusTarget ? LiveFocusM : Lens.FocusM;
                Lens.FocusM = Math.Round(LensMath.Clamp(from * Math.Pow(1.15, wheel), LensMath.MinFocus, LensMath.MaxFocus), 1);
                Lens.FocusTarget = false;
                Lens.Dof = true;
                LensChanged?.Invoke();
            }
            else if (Mathf.Abs(wheel) > 0.01f) BaseSpeed = Mathf.Clamp(BaseSpeed * Mathf.Pow(1.2f, wheel), 5f, 2000f);
            if (Lens.FocalMm > 0) ApplyFov(cam, (float)LensMath.FovFromFocal(Lens.FocalMm, Lens.SensorInfo, cam.mainCamera.aspect));

            var rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 move = ScriptMove;
            ScriptMove = Vector3.zero;
            if (!typing)
            {
                if (Input.GetKey(KeyCode.W)) move.z += 1f;
                if (Input.GetKey(KeyCode.S)) move.z -= 1f;
                if (Input.GetKey(KeyCode.D)) move.x += 1f;
                if (Input.GetKey(KeyCode.A)) move.x -= 1f;
                if (Input.GetKey(KeyCode.E)) move.y += 1f;
                if (Input.GetKey(KeyCode.Q)) move.y -= 1f;
            }
            float speed = BaseSpeed * (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 5f : 1f);
            Vector3 step = rot * new Vector3(move.x, 0f, move.z) + Vector3.up * move.y;
            Position += step * speed * dt;
            // Stay above the ground and the sea.
            float floor = CineCamera.GroundY(Position) + 2f;
            if (Position.y < floor) Position.y = floor;

            cam.transform.SetPositionAndRotation(new GlobalPosition(Position).ToLocalPosition(), rot);
            cam.cameraVelocity = Vector3.zero;
        }

        public override void FixedUpdateState(CameraStateManager cam) { }

        // The lens the free camera shows now (its own once zoomed, else the game's).
        public float CurrentFov(CameraStateManager cam) => Fov > 0 ? Fov : cam.mainCamera.fieldOfView;

        // Sets the lens in degrees (Camera Studio keys); the focal length follows.
        public void SetFov(CameraStateManager cam, float fov)
        {
            Lens.FocalMm = LensMath.FocalFromFov(fov, Lens.SensorInfo, cam.mainCamera.aspect);
            ApplyFov(cam, fov);
        }

        // The game's own field of view again (the lens panel's reset of the focal length).
        public void GameFov(CameraStateManager cam)
        {
            Lens.FocalMm = 0;
            Fov = -1;
            lensFov.Reset(cam.mainCamera);
            if (savedFov > 0) { cam.mainCamera.fieldOfView = savedFov; cam.desiredFOV = savedFov; }
        }

        private void ApplyFov(CameraStateManager cam, float fov)
        {
            Fov = fov;
            lensFov.Apply(cam, fov);
        }

        private void StartLook()
        {
            Looking = true;
            haveLastMouse = false;
            CursorManager.ForceHidden(true);          // locks and hides the cursor
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        public void StopLook()
        {
            if (!Looking) return;
            Looking = false;
            CursorManager.ForceHidden(false);
            CursorManager.Refresh();
        }
    }
}
