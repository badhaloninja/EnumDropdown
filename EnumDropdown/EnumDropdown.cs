using System;
using System.Reflection;
using System.Globalization;

using HarmonyLib;
using NeosModLoader;

using BaseX;
using FrooxEngine;
using FrooxEngine.UIX;
using FrooxEngine.LogiX;
using FrooxEngine.LogiX.Actions;
using FrooxEngine.LogiX.Cast;
using FrooxEngine.LogiX.Input;
using FrooxEngine.LogiX.Operators;
using FrooxEngine.LogiX.ProgramFlow;
using FrooxEngine.LogiX.References;
using FrooxEngine.LogiX.WorldModel;

namespace EnumDropdown
{
    public class EnumDropdown : NeosMod
    {
        public override string Name => "EnumDropdown";
        public override string Author => "badhaloninja";
        public override string Version => "1.1.0";
        public override string Link => "https://github.com/badhaloninja/EnumDropdown";

        private readonly static MethodInfo buildUI = typeof(EnumDropdown).GetMethod("BuildUi", BindingFlags.Static | BindingFlags.NonPublic); // Store this for later :)

        static readonly color salmon = new color(1f, 0.8f, 0.8f);
        static readonly color cyan = new color(0.8f, 0.8f, 1f);
        static readonly color lightGray = new color(0.8f);

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.badhaloninja.EnumDropdown");
            harmony.PatchAll();
        }
        
        [HarmonyPatch(typeof(EnumMemberEditor), "BuildUI")]
        private class EnumEditorDropdown
        {
            public static void Postfix(EnumMemberEditor __instance, RelayRef<IField> ____target, UIBuilder ui)
            {
                var root = ui.Root.GetComponentInChildren<HorizontalLayout>()?.Slot;
                if (root == null) return;

                ui.NestInto(root);

                AddDropdownBtn(ui, ____target, __instance);

                ui.NestOut();
            }
        }

        [HarmonyPatch(typeof(LogixNode), "GenerateVisual")]
        private class EnumInputDropdown
        {
            public static void Prefix(SyncRef<Slot> ____activeVisual, ref bool __state)
            {
                __state = ____activeVisual.Target == null;
            }

            public static void Postfix(LogixNode __instance, SyncRef<Slot> ____activeVisual, bool __state)
            {
                if (!__state || !__instance.GetType().IsGenericType || __instance.GetType().GetGenericTypeDefinition() != typeof(EnumInput<>)) return;

                var horiz = ____activeVisual.Target.GetComponentInChildren<HorizontalLayout>()?.Slot;
                if (horiz == null) return;

                var ui = new UIBuilder(horiz);
                ui.Style.MinWidth = 32f;

                AddDropdownBtn(ui, __instance.TryGetField("Value"));
            }
        }

        private static void AddDropdownBtn(UIBuilder ui, IField target, EnumMemberEditor editor = null)
        {
            var btn = ui.Button("▼");
            btn.Slot.DestroyWhenUserLeaves(btn.Slot.LocalUser);
            btn.Slot.PersistentSelf = false;

            // Multiplayer Support
            var logixRoot = btn.Slot.AddSlot("logix");
            // Logix Support for others clicking the button and respecting local user

            btn.DestroyWhenDestroyed(logixRoot); // Cleanup stray logix slot when packing enum input nodes


            // Logix to drive local user into button reference set
            var localUser = logixRoot.AttachComponent<LocalUser>();
            var cast = logixRoot.AttachComponent<CastClass<User, IWorldElement>>();
            var toRefId = logixRoot.AttachComponent<ReferenceID>();
            var drive = logixRoot.AttachComponent<DriverNode<RefID>>();

            cast.In.TrySet(localUser); // Cast LocalUser node to IWorldElement
            toRefId.Element.TrySet(cast); // Get RefID from IWorldElement
            drive.Source.TrySet(toRefId); // Connect RefID to input of DriveNode

            var multiplayerSupport = btn.Slot.AttachComponent<ReferenceField<User>>(); // User to run as
            var multSet = btn.Slot.AttachComponent<ButtonReferenceSet<User>>(); // Set reference field to local user on press
            multSet.TargetReference.TrySet(multiplayerSupport.Reference);

            drive.DriveTarget.TrySet(multSet.SetReference); // Drive the buttonReferenceSet to be localUser

            multiplayerSupport.Reference.OnValueChange += field =>
            { // Run as written user to respect their scale
                if (multiplayerSupport.Reference.Target == null) return; // Skip if null
                btn.Slot.RunSynchronously(() =>
                {
                    if (multiplayerSupport.Reference.Target == btn.Slot.LocalUser)
                    { // Skip if local user and reset so that local user can use clicked position 
                        multiplayerSupport.Reference.Target = null;
                        return;
                    }
                    SpawnEnumSelector(btn, target, editor, user: multiplayerSupport.Reference.Target); // SpawnEnumSelector as pressing user
                    multiplayerSupport.Reference.Target = null; // Reset
                });
            };

            // Local user spawnEnumSelector
            btn.LocalPressed += (b, e) => SpawnEnumSelector(b, target, editor, e.globalPoint);
        }

        private static void SpawnEnumSelector(IButton button, IField target, EnumMemberEditor editor, float3? globalPoint = null, User user = null)
        {
            //editor == null && (target == null || !target.ValueType.IsEnum)
            if (editor == null && (target == null || !target.ValueType.IsEnum)) return; // Skip if target is null

            if (editor != null && (editor.GetMemberValue() == null || !editor.GetMemberValue().GetType().IsEnum)) return;

            Slot enumSelector = BuildEnumSelector(target, editor); // Build Enum Selector


            // Position Selector
            UserRoot userRoot = (user == null) ? button.Slot.LocalUserRoot : user.Root; // If user is not set use local user
            

            float3 vector = button.Slot.Forward * -0.05f; // 0.05 units in front of the button (canvases are backwards)
            float3 offset = vector * userRoot.GlobalScale; // scale the vector relative to the user scale

            var rect = button.Slot.GetComponent<RectTransform>();
            var btnPos = button.Slot.GlobalPosition;
            var btnRot = button.Slot.GlobalRotation;
            // if Rect exists calculate it's center and get it's global position otherwise default to the slot position (normally center of canvas)
            if (rect != null) 
            {
                var rectCenter = rect.ComputeGlobalComputeRect().Center;
                btnPos = rect.Canvas.Slot.LocalPointToGlobal(rectCenter);
            }

            // Handle ifthe button is on the dash
            if (button.World.IsUserspace() && button.Slot.GetComponentInParents<RadiantDash>() != null)
            {
                if (enumSelector.InputInterface.ScreenActive)
                { // If user is in desktop mode overlay the selector over the dash
                    var overlay = enumSelector.World.GetGloballyRegisteredComponent<OverlayManager>();
                    if (overlay != null)
                    { // Parent selector to the overlay root and put it 1 unit in front of the dash
                        enumSelector.Parent = overlay.OverlayRoot;
                        enumSelector.GlobalPosition = float3.Backward; // 1 unit in front of dash
                        enumSelector.LocalRotation = floatQ.Identity; // reset rotation
                    };
                }
                else
                {   // If the user is in vr position selector at center of the dash 
                    var dashSlot = button.Slot.GetComponentInParents<RadiantDash>().Slot;
                    offset = dashSlot.Forward * -0.05f; 
                    enumSelector.GlobalPosition = dashSlot.GlobalPosition + offset; // set position to be 0.05 units in front of dash
                    
                    vector = enumSelector.GlobalPosition - enumSelector.World.LocalUserViewPosition; // Vector towards the user's view point
                    var normalized = vector.Normalized; 
                    enumSelector.GlobalRotation = floatQ.LookRotation(normalized); // Rotate towards the user's view point

                }
                return; // Skip the rest from executing
            }

            // If not on the dash
            enumSelector.GlobalPosition = globalPoint.GetValueOrDefault(btnPos) + offset; // Position 0.05 units in front of the globalPoint passed to the method or in front of the button position
            enumSelector.GlobalRotation = btnRot; // Rotate to be aligned with button
            enumSelector.LocalScale *= userRoot.GlobalScale; // Scale the selector relavive to the user
        }

        public static Slot BuildEnumSelector(IField target, EnumMemberEditor editor)
        {
            
            var root = target.World.LocalUserSpace.AddSlot("EnumSelector", false); // Create non-persistant root
            root.AttachComponent<Grabbable>();
            root.AttachComponent<ObjectRoot>();

            root.AttachComponent<DynamicVariableSpace>().SpaceName.Value = "EnumSelector";


            root.DestroyWhenUserLeaves(root.LocalUser);

            var ui = new UIBuilder(root, 600f, 1000f, 0.0005f);
            ui.Canvas.AcceptPhysicalTouch.Value = false;

            ui.Panel(color.White.SetA(0.8f), true); // Add white background and nest into it

            // Setup for proper scroll area with 'dynamic' element count
            ui.Style.ForceExpandHeight = false;
            ui.ScrollArea();
            ui.VerticalLayout(8f, 8f);
            ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);

            //CANCEL
            //VALUE
            //FIELD
            //BUTTON
            //ALL VALUES:
            //ValueList

            ui.Style.MinHeight = 32f;

            Type valueType = target.ValueType;
            if (editor != null)
            {
                object memberValue = editor.GetMemberValue();
                valueType = memberValue.GetType();
            }

            buildUI.MakeGenericMethod(valueType).Invoke(null, new object[] { ui, target, editor });

            return root;
        }

        private static void BuildUi<E>(UIBuilder ui, IField target, EnumMemberEditor editor) where E : Enum
        {
            if (typeof(E).IsDefined(typeof(FlagsAttribute), false))
            { // If the enum is a flag type (where you can have multiple values set at once)
                BuildFlagUi<E>(ui, target, editor);
            }
            else
            {
                BuildEnumUi<E>(ui, target, editor);
            }

            ui.Style.MinHeight = -1f; // Reset MinHeight so that the VerticalLayout does overlay other elements
            ui.VerticalLayout(8f);

            PopulateValues<E>(ui.Root, target, editor);
        }


        private static void BuildFlagUi<E>(UIBuilder ui, IField target, EnumMemberEditor editor) where E : Enum
        {
            // Destroy on cancel button pressed
            var cancel = ui.Button("Cancel", salmon);
            cancel.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = cancel.Slot.GetObjectRoot().Destroy;

            ui.Text("Value:");
            var btn = ui.Button("<i>Invalid Value</i>", cyan);

            IField<E> buttonTarget;
            E originalValue;

            DynamicValueVariable<E> setValueProxy = null;
            if (editor != null)
            {
                setValueProxy = btn.Slot.AttachComponent<DynamicValueVariable<E>>();
                setValueProxy.VariableName.Value = "proxy_value";

                object memberValue = editor.GetMemberValue();

                originalValue = memberValue != null? (E)memberValue : default;
                buttonTarget = setValueProxy.Value;
            } else
            {
                originalValue = (E)target.BoxedValue;
                buttonTarget = target as IField<E>;
            }

            // Destory On Value set
            if (editor == null)
            {
                btn.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = btn.Slot.GetObjectRoot().Destroy;
            }
            
            // Value Set
            var bvSet = btn.Slot.AttachComponent<ButtonValueSet<E>>();
            bvSet.TargetValue.Target = buttonTarget; // Point value set to target field
            bvSet.SetValue.Value = originalValue; // Initialize to the current value

            // Drive value set lable to be the selected enums
            btn.LabelTextField.DriveFrom(bvSet.SetValue, "{0}");

            // Impulse Reciever to handle flag toggle
            var lgx = btn.Slot.AddSlot("ENUMSELECTOR.TOGGLEFLAG");


            // IntToEnum breaks with enums that are set to something other than int (ColorMask for example)
            // Using ulong field and listening for changes to get around this while still having 'multiplayer support'
            var rawValue = lgx.AttachComponent<DynamicValueVariable<ulong>>();
            rawValue.VariableName.Value = "raw_value";
            
            rawValue.Value.Value = (originalValue as IConvertible).ToUInt64(CultureInfo.InvariantCulture);
            rawValue.Value.OnValueChange += field =>
            {
                bvSet.SetValue.Value = EnumUtil.UInt64ToEnum<E>(field.Value);
            };
            
            // Attach the LogiX nodes
            var impulseReciever = lgx.AttachComponent<DynamicImpulseReceiverWithValue<ulong>>();
            var xor = lgx.AttachComponent<XOR_Ulong>();
            var write = lgx.AttachComponent<WriteValueNode<ulong>>();
            var referenceNode = lgx.AttachComponent<ReferenceNode<IValue<ulong>>>();

            // Assign the LogiX nodes
            impulseReciever.Tag.TryConnectTo(lgx.NameField); // Use the slot name as the impulse tag

            // XOR the current enum value with the flag to toggle from the impulse reciever
            xor.A.TryConnectTo(rawValue.Value); 
            xor.B.TryConnectTo(impulseReciever.Value);

            write.Value.TryConnectTo(xor); // XOR into the write value
            impulseReciever.Impulse.Target = write.Write; // Connect the DynamicImpulseReciever pulse to the write node

            referenceNode.RefTarget.TrySet(rawValue.Value); // Point the reference node to the rawValue
            write.Target.TryConnectTo(referenceNode); // Connect the reference node to the write node


            ui.Text("Flags:");
        }

        private static void BuildEnumUi<E>(UIBuilder ui, IField target, EnumMemberEditor editor) where E : Enum
        {
            // Destroy on cancel button pressed
            var cancel = ui.Button("Cancel", salmon);
            cancel.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = cancel.Slot.GetObjectRoot().Destroy;

            // Value 
            ui.Text("Value:");
            var textField = ui.TextField(parseRTF: false); // Enum name field
            var btn = ui.Button("<i>Invalid Value</i>");

            /* Drive the dynvar name from the field so it can find valid enum values from the text input
             * Then relay the button event to the found enum value slot
             * Look at populate values method for more on this
             */
            var bper = btn.Slot.AttachComponent<ButtonPressEventRelay>(); 
            bper.Target.DriveFromVariable("").VariableName.DriveFrom(textField.Text.Content);

            //Setup OptionDescriptionDriver
            var opt = btn.Slot.AttachComponent<ReferenceOptionDescriptionDriver<Slot>>();
            opt.Reference.Target = bper.Target; // Make the conditional be the result of the variable driver
            opt.Label.Target = btn.LabelTextField; // Target the button label
            opt.Color.Target = btn.BaseColor; // Target the button color

            // If button target slot is not null 
            opt.DefaultOption.Label.DriveFrom(textField.Text.Content); // Drive default label to match text input
            opt.DefaultOption.Color.Value = cyan;

            // If button target driver cant find value matching the text input
            var noMatch = opt.Options.Add();
            noMatch.Label.Value = "<i>Invalid Value</i>";
            noMatch.Color.Value = color.Red.SetSaturation(0.5f); // Light red

            // End OptionDescriptionDriver setup
            ui.Text("All Values:");
        }

        private static void PopulateValues<E>(Slot valuesRoot, IField target, EnumMemberEditor editor) where E : Enum
        {
            Type enumType = typeof(E);

            if (valuesRoot == null || (editor == null && target == null) || !enumType.IsEnum) return; // If valuesRoot is null or if target is not an enum skip
            var isFlag = enumType.IsDefined(typeof(FlagsAttribute), false); // Check if target is a flagEnum
            
            var ui = new UIBuilder(valuesRoot);
            ui.Style.MinHeight = 32f;

            var enumSelectorRoot = valuesRoot.GetObjectRoot(); // The root of the EnumSelector to clean up on value selected

            var currentRawValue = valuesRoot.AttachComponent<DynamicValueVariable<ulong>>();
            currentRawValue.VariableName.Value = "raw_value";

            DynamicValueVariable<E> setValueProxy = null;

            E originalValue = default;

            if (editor!=null)
            {
                setValueProxy = valuesRoot.AttachComponent<DynamicValueVariable<E>>();
                setValueProxy.VariableName.Value = "proxy_value";
                setValueProxy.Value.OnValueChange += (field) =>
                {
                    editor.SetMemberValue(field.Value);
                    enumSelectorRoot.Destroy();
                };

                object memberValue = editor.GetMemberValue();
                originalValue = memberValue != null ? (E)memberValue : default;
            } else if (target!=null)
            {
                originalValue = (E)target.BoxedValue;
            }

            ulong originalValueUlong = (originalValue as IConvertible).ToUInt64(CultureInfo.InvariantCulture);
            var names = Enum.GetNames(enumType); // Get all names for this enum
            // Iterate over every enum value and create a button for it
            foreach (string name in names) 
            {
                E value = (E)Enum.Parse(enumType, name); // Names *must* be unique so we can just parse to get the correct value
                
                var ulongValue = (value as IConvertible).ToUInt64(CultureInfo.InvariantCulture); // Get the uint value of the enum / flag
                if (isFlag && ulongValue == 0) continue; // Skip flag 0 if it exists as it is *the* unselected value and can't be toggled


                var btn = ui.Button(name);
                btn.BaseColor.Value = name == value.ToString() ? cyan : salmon; // Set color here so the drives are setup with white, color based on if this is the first name with this value
                btn.RequireLockInToPress.Value = true; // Make it so you can scroll, I don't feel like setting up double press currently


                if (isFlag) // Handle if enum is a flag
                {
                    // Drive to be highlighted if the flag is enabled
                    var bvd = btn.Slot.AttachComponent<BooleanValueDriver<color>>();
                    bvd.TrueValue.Value = cyan;
                    bvd.FalseValue.Value = lightGray;
                    bvd.TargetField.TrySet(btn.BaseColor);

                    btn.Slot.AttachComponent<ButtonToggle>().TargetValue.TrySet(bvd.State); // Be lazy and use the button press to toggle the highlight state


                    // On pressed send impulse to toggle this flag
                    var trigger = btn.Slot.AttachComponent<ButtonDynamicImpulseTriggerWithValue<ulong>>();
                    trigger.Target.Target = enumSelectorRoot;
                    trigger.PressedData.Tag.Value = "ENUMSELECTOR.TOGGLEFLAG";
                    trigger.PressedData.Value.Value = ulongValue; // send the int value of the flag

                    currentRawValue.Value.OnValueChange += (field) =>
                    { // Update other values when raw_value changes
                        bvd.State.Value = (field.Value & ulongValue) >= ulongValue;
                    };

                    bvd.State.Value = originalValue.HasFlag(value); // Initialize the highlight state to if the flag is enabled on generate
                    continue; // Skip to the next value and ignore the nonflag code below 
                }

                // Make it so the text editor can find value buttons
                btn.Slot.CreateReferenceVariable(name, btn.Slot); // Create a slot reference with the name of the value pointing to the button slot
                var bvs = btn.Slot.AttachComponent<ButtonValueSet<E>>(); // Attach the button value set from earlier

                bvs.TargetValue.TrySet(setValueProxy != null ? setValueProxy.Value : target);
                bvs.SetValue.Value = value;

                if (editor != null) continue;
                btn.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = enumSelectorRoot.Destroy; // Cleanup the enum selector on pressed
            }
        }
    }
}