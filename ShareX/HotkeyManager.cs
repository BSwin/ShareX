#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ShareX
{
    public class HotkeyManager
    {
        private const int PrintScreenFallbackDelay = 150;
        private const int PrintScreenDuplicateThreshold = 500;

        public List<HotkeySettings> Hotkeys { get; private set; }
        public bool IgnoreHotkeys { get; set; }

        public delegate void HotkeyTriggerEventHandler(HotkeySettings hotkeySetting);
        public delegate void HotkeysToggledEventHandler(bool hotkeysEnabled);

        public HotkeyTriggerEventHandler HotkeyTrigger;
        public HotkeysToggledEventHandler HotkeysToggledTrigger;

        private HotkeyForm hotkeyForm;
        private KeyboardHook printScreenFallbackHook;
        private Timer printScreenFallbackTimer;
        private HotkeySettings pendingPrintScreenFallbackHotkey;
        private long lastPrintScreenHotkeyTick;
        private long lastPrintScreenFallbackTick;

        public HotkeyManager(HotkeyForm form)
        {
            hotkeyForm = form;
            hotkeyForm.HotkeyPress += HotkeyForm_HotkeyPress;
            hotkeyForm.FormClosed += HotkeyForm_FormClosed;

            printScreenFallbackTimer = new Timer
            {
                Interval = PrintScreenFallbackDelay
            };
            printScreenFallbackTimer.Tick += PrintScreenFallbackTimer_Tick;
        }

        private void HotkeyForm_HotkeyPress(ushort id, Keys key, Modifiers modifier)
        {
            if (key == Keys.PrintScreen)
            {
                long currentTick = Environment.TickCount64;

                if (IsWithinThreshold(currentTick, lastPrintScreenFallbackTick, PrintScreenDuplicateThreshold))
                {
                    return;
                }

                lastPrintScreenHotkeyTick = currentTick;
            }

            if (CanTriggerHotkeys())
            {
                HotkeySettings hotkeySetting = Hotkeys.Find(x => x.HotkeyInfo.ID == id);

                if (hotkeySetting != null)
                {
                    OnHotkeyTrigger(hotkeySetting);
                }
            }
        }

        private void HotkeyForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (hotkeyForm != null && !hotkeyForm.IsDisposed)
            {
                UnregisterAllHotkeys(false);
            }

            DisposePrintScreenFallbackHook();
            printScreenFallbackTimer?.Dispose();
        }

        public void UpdateHotkeys(List<HotkeySettings> hotkeys, bool showFailedHotkeys)
        {
            if (Hotkeys != null)
            {
                UnregisterAllHotkeys();
            }

            Hotkeys = hotkeys;

            RegisterAllHotkeys();
            UpdatePrintScreenFallbackHook();

            if (showFailedHotkeys)
            {
                ShowFailedHotkeys();
            }
        }

        protected void OnHotkeyTrigger(HotkeySettings hotkeySetting)
        {
            HotkeyTrigger?.Invoke(hotkeySetting);
        }

        public void RegisterHotkey(HotkeySettings hotkeySetting)
        {
            if (!Program.Settings.DisableHotkeys || hotkeySetting.TaskSettings.Job == HotkeyType.DisableHotkeys)
            {
                UnregisterHotkey(hotkeySetting, false);

                if (hotkeySetting.HotkeyInfo.Status != HotkeyStatus.Registered && hotkeySetting.HotkeyInfo.IsValidHotkey)
                {
                    hotkeyForm.RegisterHotkey(hotkeySetting.HotkeyInfo);

                    if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Registered)
                    {
                        DebugHelper.WriteLine("Hotkey registered: " + hotkeySetting);
                    }
                    else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed)
                    {
                        DebugHelper.WriteLine("Hotkey register failed: " + hotkeySetting);
                    }
                }
                else
                {
                    hotkeySetting.HotkeyInfo.Status = HotkeyStatus.NotConfigured;
                }
            }

            if (!Hotkeys.Contains(hotkeySetting))
            {
                Hotkeys.Add(hotkeySetting);
            }

            UpdatePrintScreenFallbackHook();
        }

        public void RegisterAllHotkeys()
        {
            foreach (HotkeySettings hotkeySetting in Hotkeys.ToArray())
            {
                RegisterHotkey(hotkeySetting);
            }
        }

        public void RegisterFailedHotkeys()
        {
            foreach (HotkeySettings hotkeySetting in Hotkeys.Where(x => x.HotkeyInfo.Status == HotkeyStatus.Failed))
            {
                RegisterHotkey(hotkeySetting);
            }
        }

        public void UnregisterHotkey(HotkeySettings hotkeySetting, bool removeFromList = true)
        {
            if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Registered)
            {
                hotkeyForm.UnregisterHotkey(hotkeySetting.HotkeyInfo);

                if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.NotConfigured)
                {
                    DebugHelper.WriteLine("Hotkey unregistered: " + hotkeySetting);
                }
                else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed)
                {
                    DebugHelper.WriteLine("Hotkey unregister failed: " + hotkeySetting);
                }
            }

            if (removeFromList)
            {
                Hotkeys.Remove(hotkeySetting);
            }

            UpdatePrintScreenFallbackHook();
        }

        public void UnregisterAllHotkeys(bool removeFromList = true, bool temporary = false)
        {
            if (Hotkeys != null)
            {
                foreach (HotkeySettings hotkeySetting in Hotkeys.ToArray())
                {
                    if (!temporary || hotkeySetting.TaskSettings.Job != HotkeyType.DisableHotkeys)
                    {
                        UnregisterHotkey(hotkeySetting, removeFromList);
                    }
                }
            }
        }

        public void ToggleHotkeys(bool hotkeysDisabled)
        {
            if (!hotkeysDisabled)
            {
                RegisterAllHotkeys();
            }
            else
            {
                UnregisterAllHotkeys(false, true);
            }

            UpdatePrintScreenFallbackHook();
            HotkeysToggledTrigger?.Invoke(hotkeysDisabled);
        }

        public void ShowFailedHotkeys()
        {
            List<HotkeySettings> failedHotkeysList = Hotkeys.Where(x => x.HotkeyInfo.Status == HotkeyStatus.Failed).ToList();

            if (failedHotkeysList.Count > 0)
            {
                string failedHotkeys = string.Join("\r\n", failedHotkeysList.Select(x => $"[{x.HotkeyInfo}] {x.TaskSettings}"));
                string hotkeyText = failedHotkeysList.Count > 1 ? Resources.HotkeyManager_ShowFailedHotkeys_hotkeys : Resources.HotkeyManager_ShowFailedHotkeys_hotkey;
                string text = string.Format(Resources.HotkeyManager_ShowFailedHotkeys_Unable_to_register_hotkey, hotkeyText, failedHotkeys);

                MessageBox.Show(text, "ShareX - " + Resources.HotkeyManager_ShowFailedHotkeys_Hotkey_registration_failed, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdatePrintScreenFallbackHook()
        {
            bool shouldUseFallback = Hotkeys?.Any(IsPrintScreenHotkeyCandidate) == true;

            if (shouldUseFallback)
            {
                if (printScreenFallbackHook == null)
                {
                    try
                    {
                        printScreenFallbackHook = new KeyboardHook();
                        printScreenFallbackHook.KeyUp += PrintScreenFallbackHook_KeyUp;
                        DebugHelper.WriteLine("Print Screen hotkey fallback hook started.");
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, "Print Screen hotkey fallback hook failed to start.");
                        DisposePrintScreenFallbackHook();
                    }
                }
            }
            else
            {
                DisposePrintScreenFallbackHook();
            }
        }

        private void DisposePrintScreenFallbackHook()
        {
            if (printScreenFallbackHook != null)
            {
                printScreenFallbackHook.KeyUp -= PrintScreenFallbackHook_KeyUp;
                printScreenFallbackHook.Dispose();
                printScreenFallbackHook = null;
                DebugHelper.WriteLine("Print Screen hotkey fallback hook stopped.");
            }
        }

        private void PrintScreenFallbackHook_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.PrintScreen)
            {
                return;
            }

            pendingPrintScreenFallbackHotkey = FindMatchingPrintScreenHotkey();

            if (pendingPrintScreenFallbackHotkey != null)
            {
                printScreenFallbackTimer.Stop();
                printScreenFallbackTimer.Start();
            }
        }

        private void PrintScreenFallbackTimer_Tick(object sender, EventArgs e)
        {
            printScreenFallbackTimer.Stop();

            HotkeySettings hotkeySetting = pendingPrintScreenFallbackHotkey;
            pendingPrintScreenFallbackHotkey = null;

            if (hotkeySetting == null || !CanTriggerHotkeys())
            {
                return;
            }

            long currentTick = Environment.TickCount64;

            if (IsWithinThreshold(currentTick, lastPrintScreenHotkeyTick, PrintScreenDuplicateThreshold))
            {
                return;
            }

            lastPrintScreenFallbackTick = currentTick;
            DebugHelper.WriteLine("Print Screen hotkey fallback triggered. " + hotkeySetting);
            OnHotkeyTrigger(hotkeySetting);
        }

        private HotkeySettings FindMatchingPrintScreenHotkey()
        {
            Modifiers modifiers = GetCurrentModifiers();

            return Hotkeys?.FirstOrDefault(x => IsPrintScreenHotkeyCandidate(x) && x.HotkeyInfo.ModifiersEnum == modifiers);
        }

        private bool IsPrintScreenHotkeyCandidate(HotkeySettings hotkeySetting)
        {
            return hotkeySetting?.HotkeyInfo != null &&
                hotkeySetting.TaskSettings != null &&
                hotkeySetting.HotkeyInfo.IsValidHotkey &&
                hotkeySetting.HotkeyInfo.KeyCode == Keys.PrintScreen &&
                (!Program.Settings.DisableHotkeys || hotkeySetting.TaskSettings.Job == HotkeyType.DisableHotkeys);
        }

        private bool CanTriggerHotkeys()
        {
            return !IgnoreHotkeys && (!Program.Settings.DisableHotkeysOnFullscreen || !CaptureHelpers.IsActiveWindowFullscreen());
        }

        private Modifiers GetCurrentModifiers()
        {
            Modifiers modifiers = Modifiers.None;

            if (IsKeyDown(Keys.Menu))
            {
                modifiers |= Modifiers.Alt;
            }

            if (IsKeyDown(Keys.ControlKey))
            {
                modifiers |= Modifiers.Control;
            }

            if (IsKeyDown(Keys.ShiftKey))
            {
                modifiers |= Modifiers.Shift;
            }

            if (IsKeyDown(Keys.LWin) || IsKeyDown(Keys.RWin))
            {
                modifiers |= Modifiers.Win;
            }

            return modifiers;
        }

        private bool IsKeyDown(Keys key)
        {
            return (NativeMethods.GetKeyState((int)key) & 0x8000) != 0;
        }

        private bool IsWithinThreshold(long currentTick, long previousTick, int threshold)
        {
            return previousTick > 0 && unchecked(currentTick - previousTick) <= threshold;
        }

        public void ResetHotkeys()
        {
            UnregisterAllHotkeys();
            Hotkeys.AddRange(GetDefaultHotkeyList());
            RegisterAllHotkeys();

            if (Program.Settings.DisableHotkeys)
            {
                TaskHelpers.ToggleHotkeys();
            }
        }

        public static List<HotkeySettings> GetDefaultHotkeyList()
        {
            return new List<HotkeySettings>
            {
                new HotkeySettings(HotkeyType.RectangleRegion, Keys.Control | Keys.PrintScreen),
                new HotkeySettings(HotkeyType.PrintScreen, Keys.PrintScreen),
                new HotkeySettings(HotkeyType.ActiveWindow, Keys.Alt | Keys.PrintScreen),
                new HotkeySettings(HotkeyType.ScreenRecorder, Keys.Shift | Keys.PrintScreen),
                new HotkeySettings(HotkeyType.ScreenRecorderGIF, Keys.Control | Keys.Shift | Keys.PrintScreen)
            };
        }
    }
}
