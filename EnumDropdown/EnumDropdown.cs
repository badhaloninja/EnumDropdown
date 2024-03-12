using System;
using System.Reflection;
using System.Globalization;

using HarmonyLib;
using ResoniteModLoader;

using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

using FrooxEngine.ProtoFlux;
using FrooxEngine.ProtoFlux.CoreNodes;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Users;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Actions;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Operators;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;

namespace EnumDropdown
{
    public class EnumDropdown : ResoniteMod
    {
        public override string Name => "EnumDropdown";
        public override string Author => "badhaloninja";
        public override string Version => "2.1.0";
        public override string Link => "https://github.com/badhaloninja/EnumDropdown";

        private readonly static MethodInfo buildSelectorUI = typeof(EnumDropdown).GetMethod(nameof(BuildSelectorUI), BindingFlags.Static | BindingFlags.NonPublic); // Store this for later :)

        private static ModConfiguration config;

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> showMoreEnumInfo = new("showMoreEnumInfo", "Show more information about enums in the selector", () => false);

        static readonly colorX duplicateColor = RadiantUI_Constants.Sub.PURPLE;
        static readonly colorX enumColor = RadiantUI_Constants.Sub.CYAN;
        static readonly colorX unselectedFlagColor = RadiantUI_Constants.BUTTON_COLOR;
        static readonly colorX invalidColor = RadiantUI_Constants.Sub.RED;
        public override void OnEngineInit()
        {
            config = GetConfiguration();
            Harmony harmony = new("ninja.badhalo.EnumDropdown");
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

        public static void AddDropdownBtn(UIBuilder ui, IField target, EnumMemberEditor editor)
        {
            var btn = ui.Button("▼");
            btn.Slot.DestroyWhenUserLeaves(btn.Slot.LocalUser);
            btn.Slot.PersistentSelf = false;

            // Multiplayer Support
            
            var multiplayerSupport = btn.Slot.AttachComponent<ReferenceField<User>>(); // User to run as
            var multSet = btn.Slot.AttachComponent<ButtonReferenceSet<User>>(); // Set reference field to local user on press
            multSet.TargetReference.TrySet(multiplayerSupport.Reference);

            DriveFromLocalUser(multSet.SetReference, btn.Slot);


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
            var root = target.World.LocalUserSpace.AddSlot("Enum Selector", false); // Create non-persistant root
            UIBuilder ui = RadiantUI_Panel.SetupPanel(root, "Enum Selector", new float2(640f, 1200f), true, true);

            root.LocalScale *= 0.0005f;

            RadiantUI_Constants.SetupEditorStyle(ui, false);
            ui.Canvas.AcceptPhysicalTouch.Value = false;
            ui.Style.TextAlignment = Alignment.MiddleLeft;
            ui.Style.ForceExpandHeight = false;
            root.AttachComponent<DynamicVariableSpace>().SpaceName.Value = "EnumSelector";


            root.DestroyWhenUserLeaves(root.LocalUser);


            //ui.Panel(RadiantUI_Constants.BG_COLOR, true);

            ui.ScrollArea();
            ui.VerticalLayout(8f, 8f);
            ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);

            ui.Style.MinHeight = 32f;

            Type valueType = target.ValueType;
            if (editor != null)
            {
                object memberValue = editor.GetMemberValue();
                valueType = memberValue.GetType();
            }

            buildSelectorUI.MakeGenericMethod(valueType).Invoke(null, new object[] { ui, target, editor });

            return root;
        }

        private static void BuildSelectorUI<E>(UIBuilder ui, IField target, EnumMemberEditor editor) where E : struct, Enum
        {
            var isFlag = typeof(E).IsDefined(typeof(FlagsAttribute), false);

            if (config.GetValue(showMoreEnumInfo))
            {
                var flagStr = isFlag ? "Flag" : "Enum";
                ui.Root.GetComponentInParents<GenericUIContainer>().Title.Target.Value = $"{typeof(E).Name} {flagStr}<{default(E).GetTypeCode()}>";
            }

            // Value 
            ui.Text("Value:");

            if (isFlag)
            { // If the enum is a flag type (where you can have multiple values set at once)
                BuildFlagUI<E>(ui, target, editor);
                ui.Text("Flags:");
            }
            else
            {
                BuildEnumUI<E>(ui);
                ui.Text("All Values:");
            }

            ui.Style.MinHeight = -1f; // Reset MinHeight so that the VerticalLayout does overlay other elements
            ui.VerticalLayout(8f);

            PopulateValues<E>(ui, ui.Root, target, editor);
        }

        private static void BuildFlagUI<E>(UIBuilder ui, IField target, EnumMemberEditor editor) where E : struct, Enum
        {
            var SelectedValueButton = ui.Button("", enumColor);

            E originalValue;
            IField setValue;

            string format = config.GetValue(showMoreEnumInfo) ? "{0:d}: {0}" : "{0}";

            if (editor != null)
            {
                var setValueProxy = SelectedValueButton.Slot.AttachComponent<DynamicValueVariable<E?>>();
                setValueProxy.VariableName.Value = "proxy_value";

                object memberValue = editor.GetMemberValue();

                originalValue = memberValue != null ? (E)memberValue : default;

                var valueSet = SelectedValueButton.Slot.AttachComponent<ButtonValueSet<E?>>();
                valueSet.TargetValue.Target = setValueProxy.Value; // Point value set to target field

                setValue = valueSet.SetValue;

                // Drive value set lable to be the selected enums
                SelectedValueButton.LabelTextField.DriveFrom(valueSet.SetValue, format);
            } else
            {
                var targetValue = target as IField<E>;
                originalValue = targetValue.Value;

                var valueSet = SelectedValueButton.Slot.AttachComponent<ButtonValueSet<E>>();
                valueSet.TargetValue.Target = targetValue; // Point value set to target field

                setValue = valueSet.SetValue;

                // Destory On Value set
                // If being proxied through an editor, using a destroy here blocks the value set
                // Otherwise this works here
                SelectedValueButton.Slot.AttachComponent<ButtonDestroy>().Target.TrySet(SelectedValueButton.Slot.GetObjectRoot());


                // Drive value set lable to be the selected enums
                SelectedValueButton.LabelTextField.DriveFrom(valueSet.SetValue, format);
            }
            
            setValue.BoxedValue = originalValue; // Initialize to the current value

            // Impulse Reciever to handle flag toggle
            var FluxRoot = SelectedValueButton.Slot.AddSlot("ENUMSELECTOR.TOGGLEFLAG");


            // IntToEnum breaks with enums that are set to something other than int (ColorMask for example)
            // Using ulong field and listening for changes to get around this while still having 'multiplayer support'
            var rawValue = FluxRoot.AttachComponent<DynamicValueVariable<ulong>>();
            rawValue.VariableName.Value = "raw_value";
            
            rawValue.Value.Value = (originalValue as IConvertible).ToUInt64(CultureInfo.InvariantCulture);
            rawValue.Value.OnValueChange += field =>
            {
                setValue.BoxedValue = EnumUtil.UInt64ToEnum<E>(field.Value);
            };
            
            SetupEnumXOR(rawValue.Value, FluxRoot);
        }


        private static void BuildEnumUI<E>(UIBuilder ui) where E : struct, Enum
        {
            var SearchTermField = ui.TextField(parseRTF: false);
            var SelectedValueButton = ui.Button("", colorX.White);

            /* Drive the dynvar name from the field so it can find valid enum values from the text input
             * Then relay the button event to the found enum value slot
             * Look at populate values method for more on this
             */
            var pressRelay = SelectedValueButton.Slot.AttachComponent<ButtonPressEventRelay>(); 
            pressRelay.Target.DriveFromVariable("").VariableName.DriveFrom(SearchTermField.Text.Content);

            //Setup OptionDescriptionDriver
            var descriptionDriver = SelectedValueButton.Slot.AttachComponent<ReferenceOptionDescriptionDriver<Slot>>();
            
            descriptionDriver.Label.Target = SelectedValueButton.LabelTextField; // Target the button label
            descriptionDriver.Color.Target = SelectedValueButton.BaseColor; // Target the button color
            descriptionDriver.Reference.Target = pressRelay.Target; // Make the conditional be the result of the variable driver

            // Button Target is not null
            descriptionDriver.DefaultOption.Label.DriveFromVariable("").VariableName.DriveFrom(SearchTermField.Text.Content); // Drive default label to match text input
            descriptionDriver.DefaultOption.Color.Value = enumColor;

            // If button target driver cant find value matching the text input
            var ReferenceIsNull = descriptionDriver.Options.Add();
            ReferenceIsNull.Label.Value = "<i>Invalid Value</i>";
            ReferenceIsNull.Color.Value = invalidColor; // Light red
        }

        private static void PopulateValues<E>(UIBuilder ui, Slot valuesRoot, IField target, EnumMemberEditor editor) where E : struct, Enum
        {
            Type enumType = typeof(E);

            if (valuesRoot == null || (editor == null && target == null) || !enumType.IsEnum) return; // If valuesRoot is null or if target is not an enum skip
            var isFlag = enumType.IsDefined(typeof(FlagsAttribute), false); // Check if target is a flagEnum
            
            //var ui = new UIBuilder(valuesRoot);
            ui.Style.MinHeight = 32f;

            var enumSelectorRoot = valuesRoot.GetObjectRoot(); // The root of the EnumSelector to clean up on value selected

            DynamicValueVariable<ulong> currentRawValue = null;
            if (isFlag) // Handle if enum is a flag
            {
                currentRawValue = valuesRoot.AttachComponent<DynamicValueVariable<ulong>>();
                currentRawValue.VariableName.Value = "raw_value";
            }

            DynamicValueVariable<Nullable<E>> setValueProxy = null;

            E originalValue = default;

            if (editor != null)
            {
                setValueProxy = valuesRoot.AttachComponent<DynamicValueVariable<Nullable<E>>>();
                setValueProxy.VariableName.Value = "proxy_value";

                setValueProxy.Value.OnValueChange += (field) =>
                {
                    if (field.Value == null) return;
                    editor.SetMemberValue(field.Value);
                    enumSelectorRoot.Destroy();
                };

                object memberValue = editor.GetMemberValue();
                originalValue = memberValue != null ? (E)memberValue : default;
            } else if (target != null)
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
                
                string valueLabel = config.GetValue(showMoreEnumInfo) ? string.Format("{0:d}: {1}", value, name) : name;

                var buttonColor = name == value.ToString() ? enumColor : duplicateColor; // Set color here so the drives are setup with correct values, color based on if this is the first name with this value
                var valueButton = ui.Button(valueLabel, buttonColor);

                valueButton.RequireLockInToPress.Value = true; // Make it so you can scroll, I don't feel like setting up double press currently
                
                
                if (isFlag) // Handle if enum is a flag
                {
                    // Drive to be highlighted if the flag is enabled
                    var valueSelectedHighlight = valueButton.Slot.AttachComponent<BooleanValueDriver<colorX>>();
                    var imgDrive = valueButton.ColorDrivers[0].ColorDrive.Target;

                    valueButton.ColorDrivers[0].ColorDrive.TryLink(valueSelectedHighlight.TrueValue); //.Value = cyan;

                    var falseDrive = valueButton.ColorDrivers.Add();
                    falseDrive.SetColors(unselectedFlagColor);

                    falseDrive.ColorDrive.TryLink(valueSelectedHighlight.FalseValue); //.Value = lightGray;

                    valueSelectedHighlight.TargetField.TrySet(imgDrive);


                    valueButton.Slot.AttachComponent<ButtonToggle>().TargetValue.TrySet(valueSelectedHighlight.State); // Be lazy and use the button press to toggle the highlight state


                    // On pressed send impulse to toggle this flag
                    var trigger = valueButton.Slot.AttachComponent<ButtonDynamicImpulseTriggerWithValue<ulong>>();
                    trigger.Target.Target = enumSelectorRoot;
                    trigger.PressedData.Tag.Value = "ENUMSELECTOR.TOGGLEFLAG";
                    trigger.PressedData.Value.Value = ulongValue; // send the int value of the flag

                    currentRawValue.Value.OnValueChange += (field) =>
                    { // Update other values when raw_value changes
                        valueSelectedHighlight.State.Value = (field.Value & ulongValue) >= ulongValue;
                    };

                    valueSelectedHighlight.State.Value = originalValue.HasFlag(value); // Initialize the highlight state to if the flag is enabled on generate
                    continue; // Skip to the next value and ignore the nonflag code below 
                }

                // Make it so the text editor can find value buttons
                valueButton.Slot.CreateReferenceVariable(name, valueButton.Slot);
                valueButton.Slot.CreateReferenceVariable(ulongValue.ToString(), valueButton.Slot);
                
                valueButton.Slot.CreateVariable(name, valueLabel);
                valueButton.Slot.CreateVariable(ulongValue.ToString(), valueLabel);

                var valueSet = valueButton.Slot.AttachComponent<ButtonValueSet<E?>>(); // Attach the button value set from earlier

                valueSet.TargetValue.TrySet(setValueProxy != null ? setValueProxy.Value : target);
                valueSet.SetValue.Value = value;

                if (editor != null) continue;
                valueButton.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = enumSelectorRoot.Destroy; // Cleanup the enum selector on pressed
            }
        }



        private static void DriveFromLocalUser(SyncRef<User> Target, Slot Root = null)
        {
            Root ??= Target.Slot;

            var Flux = Root.AddSlot("Local user drive");

            var localUser = Flux.AttachComponent<LocalUser>();
            var drive = (ProtoFluxNode)Flux.AttachComponent(ProtoFluxHelper.GetDriverNode(typeof(User)));

            ((IDrive)drive).TrySetRootTarget(Target); // Drive the buttonReferenceSet to be localUser

            drive.TryConnectInput(drive.GetInput(0), localUser, false, false);
            Root.DestroyWhenDestroyed(Flux); // Cleanup stray logix slot when packing enum input nodes
        }


        private static void SetupEnumXOR(Sync<ulong> Target, Slot Root)
        {
            var impulseReciever = Root.AttachComponent<DynamicImpulseReceiverWithValue<ulong>>();
            var xor = Root.AttachComponent<XOR_Ulong>();
            var write = Root.AttachComponent<ValueWrite<FrooxEngineContext,ulong>>(); // I couldn't figure out how to connect the ValueSource cleanly without FrooxEngineContext
            var source = Root.AttachComponent<FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ValueSource<ulong>>(); // The shortened type for ValueSource isn't a component
            
            source.TrySetRootSource(Target); // Point the source node to the rawValue

            impulseReciever.Tag.Target ??= Root.AttachComponent<GlobalValue<string>>();
            impulseReciever.Tag.Target.TrySetValue(Root.Name); // Use the slot name as the impulse tag

            // XOR the current enum value with the flag to toggle from the impulse reciever
            xor.A.TrySet(source);
            xor.B.TrySet(impulseReciever.Value);

            write.Value.TrySet(xor);

            impulseReciever.OnTriggered.TrySet(write.GetOperation(0)); // Connect the DynamicImpulseReciever pulse to the write node

            write.Variable.Target = source;
        }
    }
}