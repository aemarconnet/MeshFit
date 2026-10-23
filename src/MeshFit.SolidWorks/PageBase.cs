using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Plumbing shared by the MeshFit PropertyManager pages (Region Groups, the modeling wizards, Mesh Sketch):
    /// creation, refresh, control helpers and the handler interface. Every SOLIDWORKS callback is guarded, and
    /// change notifications are ignored while the page is being built or refreshed from the session.
    /// </summary>
    [ComVisible(true)]
    public abstract class PageBase : IPropertyManagerPage2Handler9
    {
        internal readonly SwAddin Addin;
        internal Session S => Addin.Session;
        protected PropertyManagerPage2 Page;
        protected bool Updating, Building;
        private bool _open;
        private int _closeReason;
        private readonly string _title;

        internal PageBase(SwAddin addin, string title)
        {
            Addin = addin;
            _title = title;
        }

        public bool IsOpen => _open;

        /// <summary>True when the page was closed with its ✖ button (not by Esc or by another page opening).</summary>
        protected bool CancelledByUser => _closeReason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Cancel;
        internal string Title => _title;

        /// <summary>OK, Cancel, Undo/Redo, locked page and pushpin (= Design X's "Lock": OK keeps the command open).</summary>
        protected virtual int Options => (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                               swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                                               swPropertyManagerPageOptions_e.swPropertyManagerOptions_UndoButton |
                                               swPropertyManagerPageOptions_e.swPropertyManagerOptions_RedoButton |
                                               swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage |
                                               swPropertyManagerPageOptions_e.swPropertyManagerOptions_PushpinButton);

        internal void Show()
        {
            if (_open) return;
            int err = 0;
            Page = Addin.App.CreatePropertyManagerPage(_title, Options, this, ref err) as PropertyManagerPage2;
            if (Page == null)
                throw new InvalidOperationException($"SOLIDWORKS could not create the {_title} page (error {err}). Open a part document and try again.");
            // SOLIDWORKS fires change notifications while controls are created and ranged: ignore them.
            Building = true;
            try
            {
                _message = null;
                Guarded.Step(_title + ": building controls");
                Build();
                _open = true;
                Opened();
                Refresh();
                Page.Show2(0);
                Guarded.Step(_title + ": open");
            }
            catch
            {
                _open = false;
                Page = null;
                throw;
            }
            finally { Building = false; }
        }

        /// <summary>Copies the session state into the controls (safe to call any time).</summary>
        internal void Refresh()
        {
            if (!_open || Page == null) return;
            Updating = true;
            try { RefreshControls(); }
            catch (Exception ex) { Guarded.Write($"{_title} refresh: {ex.Message}"); }
            finally { Updating = false; }
        }

        protected abstract void Build();
        protected abstract void RefreshControls();
        protected virtual void Opened() { }
        /// <summary>The page has gone; <paramref name="ok"/> is true for ✔ (or Apply).</summary>
        protected virtual void Closed(bool ok) { }

        /// <summary>
        /// ✔ pressed: capture what the command needs now and return the work to run once SOLIDWORKS is idle
        /// (after the page has closed, or stayed open when pinned). Null = nothing to do.
        /// </summary>
        protected virtual Action Accept() => null;

        /// <summary>After the accepted work ran while the pushpin (Lock) kept the command open.</summary>
        protected virtual void AcceptedWhilePinned() => Refresh();

        protected virtual void ButtonPressed(int id) { }
        protected virtual void CheckChanged(int id, bool check) { }
        protected virtual void OptionChecked(int id) { }
        protected virtual void NumberChanged(int id, double value) { }
        protected virtual void TextChanged(int id, string text) { }
        protected virtual void ListChanged(int id, int item) { }
        protected virtual void ComboChanged(int id, int item) { }
        protected virtual void SelectionChanged(int id, int count) { }
        protected virtual bool AcceptSelection(int id, object selection, int selType) => true;
        protected virtual void UndoPressed() => Addin.Do(Cmd.Undo);
        protected virtual void RedoPressed() => Addin.Do(Cmd.Redo);

        // ------------------------------------------------------------------ control helpers

        protected PropertyManagerPageGroup Group(int id, string caption, bool expanded = true)
        {
            int opts = (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Visible;
            if (expanded) opts |= (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded;
            return (PropertyManagerPageGroup)Page.AddGroupBox(id, caption, opts);
        }

        protected static T Add<T>(PropertyManagerPageGroup g, int id, swPropertyManagerPageControlType_e type, string caption, string tip) where T : class
        {
            int ctl = (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);
            short left = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
            return g.AddControl2(id, (short)type, caption, left, ctl, tip) as T;
        }

        protected static PropertyManagerPageButton Button(PropertyManagerPageGroup g, int id, string caption, string tip) =>
            Add<PropertyManagerPageButton>(g, id, swPropertyManagerPageControlType_e.swControlType_Button, caption, tip);

        protected static PropertyManagerPageCheckbox Check(PropertyManagerPageGroup g, int id, string caption, string tip) =>
            Add<PropertyManagerPageCheckbox>(g, id, swPropertyManagerPageControlType_e.swControlType_Checkbox, caption, tip);

        protected static PropertyManagerPageOption Option(PropertyManagerPageGroup g, int id, string caption, string tip) =>
            Add<PropertyManagerPageOption>(g, id, swPropertyManagerPageControlType_e.swControlType_Option, caption, tip);

        protected static PropertyManagerPageLabel Label(PropertyManagerPageGroup g, int id, string caption, string tip = "") =>
            Add<PropertyManagerPageLabel>(g, id, swPropertyManagerPageControlType_e.swControlType_Label, caption, tip);

        /// <summary>A caption label followed by a number box with SOLIDWORKS units (length or angle).</summary>
        protected static PropertyManagerPageNumberbox Number(PropertyManagerPageGroup g, int labelId, int id, string caption, string tip,
                                                            swNumberboxUnitType_e unit, double min, double max, double inc)
        {
            Label(g, labelId, caption);
            var n = Add<PropertyManagerPageNumberbox>(g, id, swPropertyManagerPageControlType_e.swControlType_Numberbox, "", tip);
            try { n?.SetRange2((int)unit, min, max, true, inc, inc * 10, inc / 10); } catch { /* cosmetic */ }
            return n;
        }

        protected static PropertyManagerPageListbox List(PropertyManagerPageGroup g, int id, string tip, short height)
        {
            var l = Add<PropertyManagerPageListbox>(g, id, swPropertyManagerPageControlType_e.swControlType_Listbox, "", tip);
            try { if (l != null) l.Height = height; } catch { /* cosmetic */ }
            return l;
        }

        /// <summary>Replaces a list's items only when they changed (avoids flicker while painting).</summary>
        protected static void SetItems(PropertyManagerPageListbox list, string[] items, ref string[] shown)
        {
            if (list == null) return;
            if (shown != null && shown.Length == items.Length && System.Linq.Enumerable.SequenceEqual(shown, items)) return;
            list.Clear();
            if (items.Length > 0) list.AddItems(items);
            shown = items;
        }

        // Controls are written only when their value changes: fewer SOLIDWORKS calls, and no echo notifications.
        protected static void SetChecked(PropertyManagerPageCheckbox c, bool v) { if (c != null && c.Checked != v) c.Checked = v; }
        protected static void SetChecked(PropertyManagerPageOption c, bool v) { if (c != null && c.Checked != v) c.Checked = v; }
        protected static void SetValue(PropertyManagerPageNumberbox n, double v) { if (n != null && Math.Abs(n.Value - v) > 1e-12 * Math.Max(1, Math.Abs(v))) n.Value = v; }
        protected static void SetCaption(PropertyManagerPageLabel l, string t) { if (l != null && l.Caption != t) l.Caption = t; }
        protected static void SetText(PropertyManagerPageTextbox b, string t) { if (b != null && b.Text != t) b.Text = t; }

        private string _message;

        protected void Message(string text)
        {
            if (text == _message) return;
            _message = text;
            try
            {
                Page?.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                    (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, "MeshFit");
            }
            catch { /* page closing */ }
        }

        private void Run(string what, Action a) => Guarded.Run($"{_title} {what}", a);
        private bool Quiet => Updating || Building;

        // ------------------------------------------------------------------ IPropertyManagerPage2Handler9

        public void AfterActivation() { }
        public void OnClose(int reason)
        {
            _closeReason = reason;
            if (reason != (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay &&
                reason != (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Apply) return;
            bool pinned = false;
            try { pinned = Page != null && Page.Pinned; } catch { /* older versions */ }
            Action work = null;
            Run("accept", () => work = Accept());
            if (work == null) return;
            // Build features only after SOLIDWORKS has finished closing (or keeping) the page.
            Addin.Post(() =>
            {
                Run("create", work);
                if (!pinned) return;
                if (_open) Run("refresh", AcceptedWhilePinned);   // SOLIDWORKS kept the pinned page open
                else Run("reopen", Show);                          // emulate Lock: reopen for the next feature
            });
        }

        public void AfterClose()
        {
            Guarded.Step($"{_title}: closed (reason {_closeReason})");
            _open = false;
            Page = null;
            bool ok = _closeReason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay ||
                      _closeReason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Apply;
            Run("close", () => Closed(ok));
        }

        public bool OnHelp() => false;
        public bool OnPreviousPage() => false;
        public bool OnNextPage() => false;
        public bool OnPreview() => false;
        public void OnWhatsNew() { }
        public void OnUndo() => Run("undo", UndoPressed);
        public void OnRedo() => Run("redo", RedoPressed);
        public bool OnTabClicked(int id) => true;
        public void OnGroupExpand(int id, bool expanded) { }
        public void OnGroupCheck(int id, bool check) { }
        public void OnCheckboxCheck(int id, bool check) { if (!Quiet) Run("checkbox", () => CheckChanged(id, check)); }
        public void OnOptionCheck(int id) { if (!Quiet) Run("option", () => OptionChecked(id)); }
        public void OnButtonPress(int id) { if (!Building) Run("button", () => ButtonPressed(id)); }
        public void OnTextboxChanged(int id, string text) { if (!Quiet) Run("text", () => TextChanged(id, text)); }
        public void OnNumberboxChanged(int id, double value) { if (!Quiet) Run("number", () => NumberChanged(id, value)); }
        public void OnComboboxEditChanged(int id, string text) { }
        public void OnComboboxSelectionChanged(int id, int item) { if (!Quiet) Run("combo", () => ComboChanged(id, item)); }
        public void OnListboxSelectionChanged(int id, int item) { if (!Quiet) Run("list", () => ListChanged(id, item)); }
        public void OnSelectionboxFocusChanged(int id) { }
        public void OnSelectionboxListChanged(int id, int count) { if (!Building) Run("selection", () => SelectionChanged(id, count)); }
        public void OnSelectionboxCalloutCreated(int id) { }
        public void OnSelectionboxCalloutDestroyed(int id) { }

        public bool OnSubmitSelection(int id, object selection, int selType, ref string itemText)
        {
            try { return AcceptSelection(id, selection, selType); }
            catch (Exception ex) { Guarded.Report($"{_title} selection", ex); return false; }
        }

        public int OnActiveXControlCreated(int id, bool status) => 0;
        public void OnSliderPositionChanged(int id, double value) { }
        public void OnSliderTrackingCompleted(int id, double value) { }
        public bool OnKeystroke(int wparam, int message, int lparam, int id) => false;
        public void OnPopupMenuItem(int id) { }
        public void OnPopupMenuItemUpdate(int id, ref int retval) { }
        public void OnGainedFocus(int id) { }
        public void OnLostFocus(int id) { }
        public int OnWindowFromHandleControlCreated(int id, bool status) => 0;
        public void OnListboxRMBUp(int id, int x, int y) { }
        public void OnNumberBoxTrackingCompleted(int id, double value) { }
    }
}
