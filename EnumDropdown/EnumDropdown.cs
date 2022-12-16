using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

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
        public override string Version => "1.0.1";
        public override string Link => "https://github.com/badhaloninja/EnumDropdown";

        private readonly static MethodInfo buildFlagUI = typeof(EnumDropdown).GetMethod("BuildFlagUi", BindingFlags.Static | BindingFlags.NonPublic); // Store this for later :)

        public override void OnEngineInit()
        {

            Harmony harmony = new Harmony("me.badhaloninja.EnumDropdown");
            harmony.PatchAll();
        }
        
        [HarmonyPatch(typeof(EnumMemberEditor), "BuildUI")]
        private class EnumEditorDropdown
        {
            public static void Postfix(EnumMemberEditor __instance, UIBuilder ui)
            {
                var root = ui.Root.GetComponentInChildren<HorizontalLayout>()?.Slot;
                if (root == null) return;

                ui.NestInto(root);
                var targetEnum = __instance.TryGetField("_target") as RelayRef<IField>;

                AddDropdownBtn(ui, targetEnum?.Target);

                ui.NestOut();
            }
        }
        [HarmonyPatch]
        private class EnumInputDropdown
        {
            // Harmony uses the TargetMethods method to let me generate a list of methods to patch
            // I need to do this to patch the enum input nodes because patching generics is fun :)
            static IEnumerable<MethodBase> TargetMethods() 
            {
                var namespaces = new string[] // List of namespaces to search for enums
                {
                    "FrooxEngine",
                    "BaseX",
                    "CloudX.Shared",
                    "CodeX",
                    "System.Text",
                    "System.IO",
                    "System.Net"
                    //"System"
                };
                var targetClass = typeof(EnumInput<>);
                return AccessTools.AllTypes() // Patch enum inputs (durring testing 653 enums brought it down from ~4k)
                    .Where(type =>
                            namespaces.Any(name => type?.Namespace != null && type.Namespace.StartsWith(name) || type.Namespace == "System") // If under a listed namespace OR if namespace is exactly System
                            && type.IsEnum && !type.IsGenericType) // Select all types that are an enum ignoring generic enums that apparently exist somehow
                    .Select(type => targetClass.MakeGenericType(type).GetMethod("OnGenerateVisual", BindingFlags.Instance | BindingFlags.NonPublic)) // Convert EnumInput<T> for every enum and get it's OnGenerateVisual method
                    .Cast<MethodBase>();
            }

            public static void Postfix(LogixNode __instance, Slot root)
            {
                var horiz = root.GetComponentInChildren<HorizontalLayout>()?.Slot;
                if (horiz == null) return;

                var ui = new UIBuilder(horiz);
                ui.Style.MinWidth = 32f;

                AddDropdownBtn(ui, __instance.TryGetField("Value"));
            }
        }

        private static void AddDropdownBtn(UIBuilder ui, IField target)
        {
            var btn = ui.Button("▼");

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
                    SpawnEnumSelector(btn, target, user: multiplayerSupport.Reference.Target); // SpawnEnumSelector as pressing user
                    multiplayerSupport.Reference.Target = null; // Reset
                });
            };

            // Local user spawnEnumSelector
            btn.LocalPressed += (b, e) => SpawnEnumSelector(b, target, e.globalPoint);
        }

        private static void SpawnEnumSelector(IButton button, IField target, float3? globalPoint = null, User user = null)
        {
            if (target == null || !target.ValueType.IsEnum) return; // Skip if target is null
            Slot enumSelector = BuildEnumSelector(target); // Build Enum Selector


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

        public static Slot BuildEnumSelector(IField target)
        {
            var root = target.World.LocalUserSpace.AddSlot("EnumSelector", false); // Create non-persistant root
            root.AttachComponent<Grabbable>();
            root.AttachComponent<ObjectRoot>();

            root.AttachComponent<DynamicVariableSpace>().SpaceName.Value = "EnumSelector";

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

            if (target.ValueType.IsDefined(typeof(FlagsAttribute), false))
            { // If the enum is a flag type (where you can have multiple values set at once)
                buildFlagUI.MakeGenericMethod(target.ValueType).Invoke(null, new object[] { ui, target });
            }
            else
            {
                BuildEnumUi(ui, target);
            }

            ui.Style.MinHeight = -1f; // Reset MinHeight so that the VerticalLayout does overlay other elements
            ui.VerticalLayout(8f);

            PopulateValues(ui.Root, target);
            return root;
        }
        private static void BuildFlagUi<E>(UIBuilder ui, IField target) where E : Enum
        {
            // Destroy on cancel button pressed
            var cancel = ui.Button("Cancel", new color(1f, 0.8f, 0.8f));
            cancel.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = cancel.Slot.GetObjectRoot().Destroy;

            ui.Text("Value:");
            var btn = ui.Button("<i>Invalid Value</i>", new color(0.8f, 0.8f, 1f, 1f));

            // Destory On Value set
            btn.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = btn.Slot.GetObjectRoot().Destroy;
            // Value Set
            var bvSet = btn.Slot.AttachComponent<ButtonValueSet<E>>();
            bvSet.TargetValue.Target = target as IField<E>; // Point value set to target field
            bvSet.SetValue.Value = (E)target.BoxedValue; // Initialize to the current value

            // Drive value set lable to be the selected enums
            btn.LabelTextField.DriveFrom(bvSet.SetValue, "{0}");

            // Impulse Reciever to handle flag toggle
            var lgx = btn.Slot.AddSlot("ENUMSELECTOR.TOGGLEFLAG");

            // Attach the LogiX nodes
            var reci = lgx.AttachComponent<DynamicImpulseReceiverWithValue<int>>();
            var enumToInt = lgx.AttachComponent<EnumToInt<E>>();
            var xor = lgx.AttachComponent<XOR_Int>();
            var intToEnum = lgx.AttachComponent<IntToEnum<E>>();
            var write = lgx.AttachComponent<WriteValueNode<E>>();
            var refN = lgx.AttachComponent<ReferenceNode<IValue<E>>>();

            // Assign the LogiX nodes
            reci.Tag.TryConnectTo(lgx.NameField); // The value on a string input is private so I am being lazy and using the slot name field for the dynImpuls tag
            enumToInt.Value.TryConnectTo(bvSet.SetValue); // Convert current enum value to an int

            // XOR the current enum value with the flag to toggle from the impulse reciever
            xor.A.TryConnectTo(enumToInt); 
            xor.B.TryConnectTo(reci.Value);

            intToEnum.Value.TryConnectTo(xor); // Convert the XOR result back into an enum

            write.Value.TryConnectTo(intToEnum); // Connect the enum result after the XOR into the write value
            reci.Impulse.Target = write.Write; // Connect the DynamicImpulseReciever pulse to the write node

            refN.RefTarget.TrySet(bvSet.SetValue); // Point the reference node to the current enum value
            write.Target.TryConnectTo(refN); // Connect the reference node to the write node


            ui.Text("Flags:");
        }

        private static void BuildEnumUi(UIBuilder ui, IField target)
        {
            // Destroy on cancel button pressed
            var cancel = ui.Button("Cancel", new color(1f, 0.8f, 0.8f));
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
            opt.DefaultOption.Color.Value = new color(0.8f, 0.8f, 1f); // Light cyan

            // If button target driver cant find value matching the text input
            var noMatch = opt.Options.Add();
            noMatch.Label.Value = "<i>Invalid Value</i>";
            noMatch.Color.Value = color.Red.SetSaturation(0.5f); // Light red

            // End OptionDescriptionDriver setup
            ui.Text("All Values:");
        }

        private static void PopulateValues(Slot valuesRoot, IField target)
        {
            if (valuesRoot == null || target == null || !target.ValueType.IsEnum) return; // If valuesRoot is null or if target is not an enum skip
            var isFlag = target.ValueType.IsDefined(typeof(FlagsAttribute), false); // Check if target is a flagEnum
            var values = Enum.GetValues(target.ValueType); // Get all values for this enum

            var ui = new UIBuilder(valuesRoot);
            ui.Style.MinHeight = 32f;

            var color = new color(0.8f, 0.8f, 1f); // Light cyan
            var falseColor = new color(0.8f); // Light gray
            var type = typeof(ButtonValueSet<>).MakeGenericType(target.ValueType); // Store generic type for later

            var enumSelectorRoot = valuesRoot.GetObjectRoot(); // The root of the EnumSelector to clean up on value selected


            foreach (object value in values)
            { // Iterate over every enum value and create a button for it
                var valueName = value.ToString();
                var intValue = (int)value; // Get the int value of the enum / flag
                if (isFlag && intValue == 0) continue; // Skip flag 0 if it exists as it is *the* unselected value and can't be toggled


                var btn = ui.Button(valueName);
                btn.BaseColor.Value = color; // Set color here so the drives are setup with white 
                btn.RequireLockInToPress.Value = true; // Make it so you can scroll, I don't feel like setting up double press currently


                if (isFlag) // Handle if enum is a flag
                {
                    // Drive to be highlighted if the flag is enabled
                    var bvd = btn.Slot.AttachComponent<BooleanValueDriver<color>>();
                    bvd.TrueValue.Value = color;
                    bvd.FalseValue.Value = falseColor;
                    bvd.TargetField.TrySet(btn.BaseColor);

                    btn.Slot.AttachComponent<ButtonToggle>().TargetValue.TrySet(bvd.State); // Be lazy and use the button press to toggle the highlight state


                    // On pressed send impulse to toggle this flag
                    var trigger = btn.Slot.AttachComponent<ButtonDynamicImpulseTriggerWithValue<int>>();
                    trigger.Target.Target = enumSelectorRoot;
                    trigger.PressedData.Tag.Value = "ENUMSELECTOR.TOGGLEFLAG";
                    trigger.PressedData.Value.Value = intValue; // send the int value of the flag

                    bvd.State.Value = ((int)target.BoxedValue & intValue) != 0; // Initialize the highlight state to if the flag is enabled on generate
                    continue; // Skip to the next value and ignore the nonflag code below 
                }

                // Make it so the text editor can find value buttons
                btn.Slot.CreateReferenceVariable(valueName, btn.Slot); // Create a slot reference with the name of the value pointing to the button slot
                var bvs = btn.Slot.AttachComponent(type); // Attach the button value set from earlier

                (bvs.TryGetField("TargetValue") as ISyncRef).Target = target; // Target the original enum field
                bvs.TryGetField("SetValue").BoxedValue = value; // Set the button value set to this value

                btn.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = enumSelectorRoot.Destroy; // Cleanup the enum selector on pressed
            }
        }
    }
}