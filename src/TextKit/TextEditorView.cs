using SkiaSharp;
using System;
using System.Data.Common;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices.ComTypes;
using Topten.GuiKit.DataTransfer;
using Miko.TextKit;
using Miko.TextKit.Editor;

namespace Miko.TextKit
{
    /// <summary>
    /// Core text editor functionality
    /// </summary>
    public class TextEditorView : View, ITextDocumentView
    {
        /// <summary>
        /// Constructs a new TextEditor
        /// </summary>
        public TextEditorView()
        {
            Width = AutoSize.FillParent;
            Height = AutoSize.FillParent;
            RenderMode = RenderMode.Rasterized;
            FillBackground = Color.White;
            TakesFocus = true;
            ShouldClip = true;
            _caretView = new CaretView();
            _document = new TextDocument();
            _document.RegisterView(this);
        }

        /// <summary>
        /// Gets or sets the document to be edited by this view
        /// </summary>
        public TextDocument Document
        {
            get => _document;
            set
            {
                if (value == null)
                    value = new TextDocument();

                _document.RevokeView(this);
                _document = value;
                _document.RegisterView(this);

                SetSelection(new TextRange(0));
                Invalidate();
                UpdateContentSize();
            }
        }

        /// <summary>
        /// Select the entire document
        /// </summary>
        public void SelectAll()
        {
            SetSelection(new TextRange(0, _document.Length));
        }

        /// <summary>
        /// Configures the editor to password mode
        /// </summary>
        /// <remarks>
        /// Password mode disables various actions like copying
        /// from the text field as well as disabling word based navigation
        /// </remarks>
        public bool PasswordMode
        {
            get;
            set;
        }

        /// <summary>
        /// Specifies that this text field should be read-only
        /// </summary>
        public bool ReadOnly
        {
            get => ControlState.HasFlag(ControlState.ReadOnly);
            set
            {
                ControlState = (ControlState & ~ControlState.ReadOnly) | (value ? ControlState.ReadOnly : 0);
            }
        }


        /// <summary>
        /// Specifies the selected range of text
        /// </summary>
        public TextRange Selection
        {
            get
            {
                return _selection;
            }
            set
            {
                value = SetSelection(value);
            }
        }

        /// <summary>
        /// Specifies the color to be used for selected text highlights
        /// </summary>
        public Color SelectionColor
        {
            get => _selectionColor;
            set
            {
                if (_selectionColor != value)
                {
                    _selectionColor = value;
                    if (_selection.IsRange)
                        Invalidate();
                }
            }
        }

        /// <summary>
        /// Notifies that the selection has changed
        /// </summary>
        public event Action SelectionChanged;

        /// <summary>
        /// Notifies that the selection has changed
        /// </summary>
        protected virtual void OnSelectionChanged()
        {
            SelectionChanged?.Invoke();
        }


        /// <summary>
        /// Specifies the color to be used for the caret
        /// </summary>
        public Color CaretColor
        {
            get => _caretView.Color;
            set => _caretView.Color = value;
        }

        /// <inheritdoc />
        protected override void OnScaleChanged()
        {
            base.OnScaleChanged();
            UpdateCaret();
        }

        /// <inheritdoc />
        protected override void OnSetCursor(Vector pt)
        {
            // Called when OS wants the cursor updated
            SetCursor();
        }

        /// <inheritdoc />
        protected override void OnDraw(DrawingContext ctx)
        {
            // Paint Background
            base.OnDraw(ctx);

            /*
            if (_lastChangeHeight < 0)
            {
                ctx.FillRectangle(new Rectangle(0, _lastChangeYCoord, VisibleBounds.Width, -_lastChangeHeight), Color.Red.WithAlpha(0.5f));
            }
            else if (_lastChangeHeight > 0)
            {
                ctx.FillRectangle(new Rectangle(0, _lastChangeYCoord, VisibleBounds.Width, _lastChangeHeight), Color.Green.WithAlpha(0.5f));
            }
            */

            // In overtype mode, paint the character that will be overtyped
            // in the selection highlight
            var highlightRange = _selection;
            if (_overtype && !_document.IsImeComposing)
                highlightRange = _document.GetOvertypeRange(highlightRange);

            // Setup to paint selection range
            TextPaintOptions opts = null;
            if (highlightRange.IsRange)
            {
                opts = new TextPaintOptions();
                opts.Selection = highlightRange;
                opts.SelectionColor = _selectionColor.ToSK();
            }

            // Paint the document
            _document.Paint(ctx.Canvas, (float)VisibleBounds.Top, (float)VisibleBounds.Bottom, opts);
        }

        /// <inheritdoc />
        protected override void OnFocusPresenceChanged()
        {
            // When we get focus show caret, when 
            // we lose focus hide the caret
            UpdateCaret();
        }

        /// <inheritdoc />
        protected override void OnBoundsChanged()
        {
            _document.PageWidth = (float)VisibleBounds.Width;
            UpdateContentSize();
            UpdateCaret();
            base.OnBoundsChanged();
        }

        /// <inheritdoc />
        protected override void OnPointerEvent(PointerEvent e, int pointerId, Vector pt)
        {
            // When the mouse is moved, show the mouse cursor
            if (GetPointerKind(pointerId) == PointerKind.Mouse && e == PointerEvent.Over)
            {
                SetCursorVisible(true);
            }

            // Pointer click?
            switch (e)
            {
                case PointerEvent.PrimaryDown:
                case PointerEvent.PrimaryDouble:
                    TakeFocus(true);

                    if (_trackingPointer == -1)
                    {
                        // Track pointer
                        CapturePointer(pointerId);
                        _trackingPointer = pointerId;

                        // Work out selection kind:
                        // - single click in margin = line selection  
                        // - double click in margin = paragraph selection  
                        // - double click in paragraph = word selection
                        // - else regular character selection
                        SelectionKind selKind;
                        if (pt.X < _document.MarginLeft)
                        {
                            selKind = e == PointerEvent.PrimaryDouble ? SelectionKind.Paragraph : SelectionKind.Line;
                        }
                        else
                        {
                            if (PasswordMode)
                                selKind = e == PointerEvent.PrimaryDouble ? SelectionKind.Paragraph : SelectionKind.None;
                            else
                                selKind = e == PointerEvent.PrimaryDouble ? SelectionKind.Word : SelectionKind.None;
                        }

                        // Setup drag handler
                        _pointerDragHandler = new MouseSelectionDragHandler(this, selKind);
                        _pointerDragHandler.Down(pt);
                    }
                    break;

                case PointerEvent.Drag:
                    if (_trackingPointer == pointerId)
                    {
                        // Tell scroll view to do auto-scrolling near edges
                        SuperviewOfType<ScrollView>()?.DoAutoScroll(this, pointerId, pt);

                        // Pass to drag handler
                        _pointerDragHandler.Drag(pt);
                    }
                    break;

                case PointerEvent.PrimaryUp:
                case PointerEvent.Cancel:
                    if (_trackingPointer == pointerId)
                    {
                        // Finish auto scrolling
                        SuperviewOfType<ScrollView>()?.FinishAutoScroll();

                        // Cancel tracking
                        CancelPointer(pointerId);
                        _trackingPointer = -1;

                        // Pass to drag handler
                        _pointerDragHandler?.Up(pt);
                        _pointerDragHandler = null;

                        // During auto-scroll EnsureVisible requests are ignored 
                        // by the scroll view.  Now that the auto scroll has 
                        // finished, make sure the caret is now visible.
                        _caretView.EnsureVisible();
                    }
                    break;
            }

            base.OnPointerEvent(e, pointerId, pt);
        }

        /// <inheritdoc />
        protected override void OnKeyEvent(Key key)
        {
            // On any key event hide the mouse cursor
            if (key.IsCharacters)
            {
                if (ReadOnly)
                    return;

                SetCursorVisible(false);

                // Don't insert control characters
                if (key.Characters[0] < 32 || key.Characters[0] == 0x7f)
                    return;

                //                Console.WriteLine($"Chars: '{key.Characters}'");
                OnType(key.Characters);
            }

            // Handle keyboard navigation
            if (key.IsPress)
            {
                // Ctrl+A to Select All
                if (key.Token == ShortcutKey.FormatToken(KeyModifier.Control, 'A'))
                {
                    SelectAll();
                    return;
                }

                switch (key.KeyCode)
                {
                    case KeyCode.Return:
                        if (ReadOnly)
                            break;

                        if (Keyboard.IsKeyPressed(KeyCode.Shift))
                        {
                            // Soft line break
                            OnType("\n");
                        }
                        else
                        {
                            // Hard paragraph break
                            _document.ReplaceText(this, _selection, "\u2029", EditSemantics.Typing);
                        }
                        break;

                    case KeyCode.Left:
                        if (Keyboard.IsKeyPressed(KeyCode.Control))
                            Navigate(PasswordMode ? NavigationKind.LineHome : NavigationKind.WordLeft);
                        else
                            Navigate(NavigationKind.CharacterLeft);
                        break;

                    case KeyCode.Right:
                        if (Keyboard.IsKeyPressed(KeyCode.Control))
                            Navigate(PasswordMode ? NavigationKind.LineEnd : NavigationKind.WordRight);
                        else
                            Navigate(NavigationKind.CharacterRight);
                        break;

                    case KeyCode.Up:
                        Navigate(NavigationKind.LineUp);
                        break;

                    case KeyCode.Down:
                        Navigate(NavigationKind.LineDown);
                        break;

                    case KeyCode.PageUp:
                        Navigate(NavigationKind.PageUp);
                        break;

                    case KeyCode.PageDown:
                        Navigate(NavigationKind.PageDown);
                        break;

                    case KeyCode.Home:
                        if (Keyboard.IsKeyPressed(KeyCode.Control))
                            Navigate(NavigationKind.DocumentHome);
                        else
                            Navigate(NavigationKind.LineHome);
                        break;

                    case KeyCode.End:
                        if (Keyboard.IsKeyPressed(KeyCode.Control))
                            Navigate(NavigationKind.DocumentEnd);
                        else
                            Navigate(NavigationKind.LineEnd);
                        break;

                    case KeyCode.Delete:
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.Shift)
                            OnCut();
                        else if (key.Modifiers == KeyModifier.None)
                            OnDelete();
                        break;

                    case KeyCode.Backspace:
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.None)
                            OnBackspace();
                        break;

                    case KeyCode.Z:
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.Control)
                            OnUndo();
                        else if (key.Modifiers == (KeyModifier.Control | KeyModifier.Shift))
                            OnRedo();
                        break;

                    case KeyCode.Y:
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.Control)
                            OnRedo();
                        break;

                    case KeyCode.C:
                        if (key.Modifiers == KeyModifier.Control)
                            OnCopy();
                        break;

                    case KeyCode.X:
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.Control)
                            OnCut();
                        break;

                    case KeyCode.V:
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.Control)
                            OnPaste();
                        break;

                    case KeyCode.Insert:
                        if (key.Modifiers == KeyModifier.Control)
                            OnCopy();
                        if (ReadOnly)
                            break;
                        if (key.Modifiers == KeyModifier.Shift)
                            OnPaste();
                        if (key.Modifiers == KeyModifier.None)
                        {
                            _overtype = !_overtype;
                            Invalidate();
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        protected override bool OnImeStart(bool canCompose)
        {
            if (ReadOnly)
                return false;

            // Set IME editor font
            var style = _document.GetStyleAtOffset(_selection.End);
            SetImeFont(new Font(style.FontFamily, style.FontSize * this.DpiContent)
            {
                Weight = style.FontWeight,
            });

            // We want custom IME supprot
            return true;
        }

        /// <inheritdoc />
        public override bool OnEnsureVisible(View view, Rectangle r)
        {
            // Give scrollview super view first crack
            if (Superview != null && Superview.OnEnsureVisible(view, r))
                return true;

            // Do it ourself
            AdjustContentOffsetToShowRect(view.ConvertCoordsTo(this, r));
            return true;
        }

        /// <inheritdoc />
        protected override void OnContextMenu(PointerKind pointerKind, View subView, Vector? point)
        {
            var menu = new Menu()
            {
                InitItems = new MenuBase[]
                {
                    new MenuItem("&Undo".T()) { Clicked = (mi) => OnUndo(), Enabled = _document.UndoManager.CanUndo && !ReadOnly },
                    new MenuItem("&Redo".T()) { Clicked = (mi) => OnRedo(), Enabled = _document.UndoManager.CanRedo && !ReadOnly },
                    new MenuSeparator(),
                    new MenuItem("Cu&t".T()) { Clicked = (mi) => OnCut(), Enabled = _selection.IsRange && !PasswordMode && !ReadOnly},
                    new MenuItem("&Copy".T()) { Clicked = (mi) => OnCopy(), Enabled = _selection.IsRange && !PasswordMode},
                    new MenuItem("&Paste".T()) { Clicked = (mi) => OnPaste(), Enabled = CanPaste  },
                    new MenuItem("&Delete".T()) { Clicked = (mi) => OnDelete(), Enabled = _selection.IsRange && !ReadOnly},
                    new MenuSeparator(),
                    new MenuItem("Select &All".T()) { Clicked = (mi) => SelectAll() },
                }
            };

            TrackPopupMenu(menu, point ?? new Vector(10, 10), pointerKind);
        }


        /// <inheritdoc />
        protected override void OnImeComposition(ImeCompositionString str)
        {
            if (ReadOnly)
                return;

            if (str.Kind == ImeComposeKind.Finish)
            {
                // Undo our temporary composition changes
                // NB: this will also restore selection to whatever it 
                //     was before composition started
                _document.FinishImeComposition(this);

                // Insert the text
                _document.ReplaceText(this, _selection, str.FullString, EditSemantics.Typing);
                _document.UndoManager.Seal();
            }
            else
            {
                // Notify document we're starting IME composition and where
                // in the document the string will be inserted.
                // Because the caret can move about within the composed string
                // we can't rely on _selection during the composition
                if (!_document.IsImeComposing)
                    _document.StartImeComposition(this, _selection);

                // Build a styled string using the composed string attributes
                var text = new StyledText();
                if (str.Attributes != null)
                {
                    // Start with the style at the insertion point
                    var baseStyle = _document.GetStyleAtOffset(_document.ImeCompositionOffset);
                    var styleManager = StyleManager.Default.Value;

                    // Build string with underlined clauses
                    foreach (var ar in str.Attributes)
                    {
                        styleManager.CurrentStyle = baseStyle;
                        styleManager.ReplacementCharacter('\0');
                        var style = styleManager.Underline(UnderlineStyleForImeAttribute(ar.Kind));
                        text.AddText(str.FullString.Substring(ar.Offset, ar.Length), style);
                    }
                }
                else
                {
                    // No attributes, insert as plain text
                    text.AddText(str.FullString, null);
                }

                // Update the document
                _document.UpdateImeComposition(this, text, text.CharacterToCodePointIndex(str.CaretPosition));
            }
            base.OnImeComposition(str);
        }

        /// <summary>
        /// Convert a ImeCompositionString.Attribute into the associated
        /// RichTextKit underline style
        /// </summary>
        /// <param name="attribute">The attribute to convert</param>
        /// <returns>The associated underline style</returns>
        UnderlineStyle UnderlineStyleForImeAttribute(ImeCompositionString.Attribute attribute)
        {
            switch (attribute)
            {
                case ImeCompositionString.Attribute.Input:
                    return UnderlineStyle.ImeInput;

                case ImeCompositionString.Attribute.Converted:
                    return UnderlineStyle.ImeConverted;

                case ImeCompositionString.Attribute.TargetConverted:
                    return UnderlineStyle.ImeTargetConverted;

                case ImeCompositionString.Attribute.TargetNonConverted:
                    return UnderlineStyle.ImeTargetNonConverted;
            }

            return UnderlineStyle.None;
        }

        /// <summary>
        /// Move the caret in response to a navigation keystroke
        /// </summary>
        /// <param name="kind">The kind of caret move</param>
        void Navigate(NavigationKind kind)
        {
            // Extend selection?
            bool extend = Keyboard.IsKeyPressed(KeyCode.Shift);

            // If cancelling selection the navigation starts from whichever
            // end of the selection is in the direction of the navigation.
            // eg: if navigating left, start navigation from the end of
            //     the selection closer to the start of the document
            if (_selection.IsRange && !extend)
            {
                // Swap the selection range if moving from the other end
                if (_selection.Start > _selection.End != IsLeftwardNavigation(kind))
                {
                    _selection = _selection.Reversed;
                }

                // For CharacterLeft/Right when have a selection, the caret just 
                // moves to the end of the selection - it doesn't then move by 
                // a character so set the nav kind to none.
                if (kind == NavigationKind.CharacterLeft || kind == NavigationKind.CharacterRight)
                {
                    kind = NavigationKind.None;
                }
            }

            // Navigate from current position to new position
            var ghostXCoord = _ghostXCoord;
            var oldPos = _selection.CaretPosition;
            var newPos = _document.Navigate(oldPos, kind, (float)VisibleBounds.Height, ref ghostXCoord);

            // When moving vertically and extending selection and we hit the top or bottom of the 
            // document, instead of stopping mid-line, move to the document home/end.
            if (extend && oldPos.CodePointIndex == newPos.CodePointIndex)
            {
                switch (kind)
                {
                    case NavigationKind.LineUp:
                    case NavigationKind.PageUp:
                        newPos = _document.Navigate(oldPos, NavigationKind.DocumentHome, (float)VisibleBounds.Height, ref ghostXCoord);
                        break;

                    case NavigationKind.LineDown:
                    case NavigationKind.PageDown:
                        newPos = _document.Navigate(oldPos, NavigationKind.DocumentEnd, (float)VisibleBounds.Height, ref ghostXCoord);
                        break;
                }
            }

            // Move caret
            MoveCaret(newPos, extend);

            // Store ghost position 
            // (do this after call to MoveCaret as it clears the ghost pos)
            _ghostXCoord = ghostXCoord;
        }

        TextRange SetSelection(TextRange value, bool fireEvent = true)
        {
            // Clamp valid
            value = value.Clamp(_document.Length - 1);

            // Clear the ghost position
            _ghostXCoord = null;

            // Remember if there was a selection
            bool hadSelection = _selection.IsRange;

            // Store new selection
            _selection = value;

            // If selection range was displayed, or is displayed then we need to repaint
            bool haveSelection = _selection.IsRange;
            if (hadSelection || haveSelection || _overtype)
            {
                Invalidate();
            }

            // Reposition the caret
            UpdateCaret();

            // Scroll to ensure the caret is visible
            _caretView.EnsureVisible();

            if (fireEvent)
                OnSelectionChanged();
            return value;
        }

        /// <summary>
        /// Move the caret to a new code point index, optionally extending the
        /// selection.
        /// </summary>
        /// <param name="position">The position to move the caret to</param>
        /// <param name="extend">Whether to extend the selection or not</param>
        void MoveCaret(CaretPosition position, bool extend)
        {
            if (extend)
            {
                SetSelection(new TextRange(Selection.Start, position.CodePointIndex, position.AltPosition));
            }
            else
            {
                SetSelection(new TextRange(position));
            }
        }

        /// <summary>
        /// Helper to show/hide the caret and position it correctly when shown
        /// </summary>
        void UpdateCaret()
        {
            if (ActiveFocusPresence != Presence.Present)
            {
                if (_caretView.Superview != null)
                {
                    // No focus, remove the caret
                    _caretView.RemoveFromSuperview();
                    SetImePosition(null);
                }
            }
            else
            {
                // Get caret position
                var ci = _document.GetCaretInfo(_selection.CaretPosition);

                // Convert to GuiKit rectangle and inflate to caret width if vertical
                var r = ci.CaretRectangle.ToGK();
                if (ci.CaretRectangle.Width == 0)
                {
                    // RichTextKit returns a zero width rectangle for non-italic caret
                    // so adjust width
                    r = r.Inflate(_caretView.CaretWidth, 0);
                    _caretView.Italic = false;
                }
                else
                {
                    _caretView.Italic = true;
                }

                // Setup caret position
                _caretView.Frame = r;

                // Report to OS for IME positioning
                SetImePosition(r);

                // Add as subview
                if (_caretView.Superview == null)
                {
                    SubViews.Add(_caretView);
                }
                else
                {
                    _caretView.ResetFlash();
                }
            }
        }

        /// <summary>
        /// Updates the content size of this view based on the measured
        /// size of the document so that scrolling works correctly.
        /// </summary>
        void UpdateContentSize()
        {
            if (_document.LineWrap)
            {
                ContentSize = new Size(0, _document.MeasuredHeight);
            }
            else
            {
                ContentSize = new Size(_document.MeasuredWidth, _document.MeasuredHeight);
            }
        }

        /// <summary>
        /// Helper to hide/show the mouse cursor
        /// </summary>
        /// <param name="show">Whether the cursor should be shown or not</param>
        void SetCursorVisible(bool show)
        {
            if (ReadOnly)
                show = true;

            if (_showCursor != show)
            {
                _showCursor = show;
                SetCursor();
            }
        }

        /// <summary>
        /// Helper to actually set the correct cursor depending whether
        /// current visible or not
        /// </summary>
        void SetCursor()
        {
            if (_showCursor)
                SetCursor(CursorKind.IBeam);
            else
                SetCursor(CursorKind.None);
        }

        /// <summary>
        /// Delete the selected text, or forward delete one character if no selection
        /// </summary>
        void OnDelete()
        {
            if (ReadOnly)
                return;

            var semantics = EditSemantics.None;

            // If no selection, extend the selection to the next character
            if (!_selection.IsRange)
            {
                var extendTo = _document.Navigate(_selection.CaretPosition, NavigationKind.CharacterRight, 0, ref _ghostXCoord);
                _selection = TextRange.Union(_selection, new TextRange(extendTo));

                if (!_selection.IsRange)
                    return;

                semantics = EditSemantics.ForwardDelete;
            }

            // Delete the text
            _document.ReplaceText(this, _selection, null, semantics);
        }

        /// <summary>
        /// Delete the selected text, or backspace one character if no selection
        /// </summary>
        void OnBackspace()
        {
            if (ReadOnly)
                return;

            // If no selection, extend the selection to the previous character
            var semantics = EditSemantics.None;
            if (!_selection.IsRange)
            {
                var extendTo = _document.Navigate(_selection.CaretPosition, NavigationKind.CharacterLeft, 0, ref _ghostXCoord);
                _selection = TextRange.Union(_selection, new TextRange(extendTo));
                if (!_selection.IsRange)
                    return;

                semantics = EditSemantics.Backspace;
            }

            // Delete the text
            _document.ReplaceText(this, _selection, null, semantics);
        }

        /// <summary>
        /// Insert text into the document at the caret position, replacing the selected text if any
        /// </summary>
        /// <param name="text"></param>
        void OnType(string text)
        {
            if (ReadOnly)
                return;

            // Insert the text
            _document.ReplaceText(this, _selection, text, _overtype ? EditSemantics.Overtype : EditSemantics.Typing);
        }

        /// <summary>
        /// Copy the current selection and place it on the clipboard
        /// </summary>
        /// <returns>True if there was a selection; otherwise false</returns>
        bool OnCopy()
        {
            if (PasswordMode)
                return false;

            // Only if have a selection range
            if (!_selection.IsRange)
                return false;

            // Get the text
            var text = _document.GetText(_selection);
            text.Replace(0x2029, '\n');

            // Put it on the clipboard
            var data = new DataTransfer.DataBundle();
            data.Add(DataFormats.Text, text.ToString());
            data.SetClipboard();
            return true;
        }

        /// <summary>
        /// Cut the current selection to the clipboard
        /// </summary>
        void OnCut()
        {
            if (ReadOnly)
                return;

            if (OnCopy())
                OnDelete();
        }

        /// <summary>
        /// Check if clipboard contains pastable data
        /// </summary>
        bool CanPaste
        {
            get
            {
                if (ReadOnly)
                    return false;
                // Get text from clipboard
                var data = DataTransfer.DataBundle.FromClipboard();
                return data?.QueryFormat(DataFormats.Text) ?? false;
            }
        }

        /// <summary>
        /// Paste the clipboard contents into the document
        /// </summary>
        void OnPaste()
        {
            if (ReadOnly)
                return;

            // Get text from clipboard
            var data = DataTransfer.DataBundle.FromClipboard();
            var text = (string)data?.GetData(typeof(string), DataFormats.Text);

            // Insert it
            if (text != null)
            {
                // Clean up CRLF and convert to paragraph separators
                text = text.Replace("\r\n", "\u2029").Replace('\n', '\u2029');

                // We want same selection semantics as typing (ie: move caret to end)
                _document.ReplaceText(this, _selection, text, EditSemantics.Typing);

                // but we don't want to be able to extend unit, so seal that last unit
                _document.UndoManager.Seal();
            }
        }

        void OnUndo()
        {
            if (ReadOnly)
                return;

            _document.Undo(this);
        }

        void OnRedo()
        {
            if (ReadOnly)
                return;

            _document.Redo(this);
        }

        /// <summary>
        /// Helper to check if a navigation kind is "leftward" - ie: towards
        /// the start of the document
        /// </summary>
        /// <param name="kind">The navigation kind to check</param>
        /// <returns>True if the navigation is leftward</returns>
        static bool IsLeftwardNavigation(NavigationKind kind)
        {
            switch (kind)
            {
                case NavigationKind.CharacterLeft:
                case NavigationKind.WordLeft:
                case NavigationKind.LineUp:
                case NavigationKind.LineHome:
                case NavigationKind.PageUp:
                case NavigationKind.DocumentHome:
                    return true;
            }

            return false;
        }

        /// <inheritdoc />
        void ITextDocumentView.OnReset()
        {
            Invalidate();
            UpdateContentSize();
            UpdateCaret();
            Selection = new TextRange(0);
            ContentOffset = Vector.Zero;
        }

        /// <inheritdoc />
        void ITextDocumentView.OnRedraw()
        {
            Invalidate();
            UpdateContentSize();
            UpdateCaret();
        }

        /// <inheritdoc />
        void ITextDocumentView.OnDocumentWillChange(ITextDocumentView view)
        {
            // Default pending selection it the current selection
            _pendingSelection = _selection;

            // Track height changes to document
            _pendingContentOffset = ContentOffset;
            _lastHeight = _document.MeasuredHeight;
        }

        /// <inheritdoc />
        void ITextDocumentView.OnDocumentChange(ITextDocumentView view, DocumentChangeInfo info)
        {
            if (view == this)
            {
                switch (info.Semantics)
                {
                    case EditSemantics.None:
                        _pendingSelection = new TextRange(info.CodePointIndex, info.CodePointIndex + info.NewLength);
                        break;

                    case EditSemantics.Backspace:
                        if (info.IsUndoing)
                            _pendingSelection = new TextRange(info.CodePointIndex + info.NewLength);
                        else
                            _pendingSelection = new TextRange(info.CodePointIndex);
                        break;

                    case EditSemantics.ForwardDelete:
                        _pendingSelection = new TextRange(info.CodePointIndex);
                        break;

                    case EditSemantics.Typing:
                        if (info.IsUndoing)
                            _pendingSelection = new TextRange(info.CodePointIndex, info.CodePointIndex + info.NewLength);
                        else
                            _pendingSelection = new TextRange(info.CodePointIndex + info.NewLength);
                        break;

                    case EditSemantics.Overtype:
                        if (info.IsUndoing)
                            _pendingSelection = new TextRange(info.CodePointIndex, info.CodePointIndex);
                        else
                            _pendingSelection = new TextRange(info.CodePointIndex + info.NewLength);
                        break;

                    case EditSemantics.ImeComposition:
                        if (info.IsUndoing)
                            _pendingSelection = new TextRange(info.CodePointIndex, info.CodePointIndex + info.NewLength);
                        else
                            _pendingSelection = new TextRange(info.CodePointIndex + info.ImeCaretOffset);
                        break;
                }
            }
            else
            {
                // Update the selection
                _pendingSelection = _pendingSelection.UpdateForEdit(info.CodePointIndex, info.OldLength, info.NewLength);

                // If the document height changed, then work out if it was before
                // the region we have on view and if so, adjust our content offset
                // so that what we have on view stays in the same position
                var deltaHeight = _document.MeasuredHeight - _lastHeight;
                _lastHeight = _document.MeasuredHeight;
                if (deltaHeight != 0)
                {
                    var changeYCoord = _document.GetCaretInfo(new CaretPosition(info.CodePointIndex)).CaretRectangle.Top;
                    if (changeYCoord < -_pendingContentOffset.Y)
                        _pendingContentOffset = new Vector(_pendingContentOffset.X, _pendingContentOffset.Y - deltaHeight);
                }
            }
        }

        /// <inheritdoc />
        void ITextDocumentView.OnDocumentDidChange(ITextDocumentView view)
        {
            // Update for changes made during edit
            UpdateContentSize();
            ContentOffset = _pendingContentOffset;
            SetSelection(_pendingSelection, true);

            // Repaint
            Invalidate();
        }

        // Abstract base class for all pointer drag handlers
        abstract class PointerDragHandler
        {
            public abstract void Down(Vector pt);
            public abstract void Drag(Vector pt);
            public abstract void Up(Vector pt);
        }

        // Pointer handler for mouse drag
        class MouseSelectionDragHandler : PointerDragHandler
        {
            public MouseSelectionDragHandler(TextEditorView owner, SelectionKind selectionKind)
            {
                _owner = owner;
                _selectionKind = selectionKind;
            }

            public override void Down(Vector pt)
            {
                // Hit test the document
                var htr = _owner._document.HitTest((float)pt.X, (float)pt.Y);

                // Extend the selection according to the selection kind
                _originalSelection = _owner._document.GetSelectionRange(htr.CaretPosition, _selectionKind);

                // Extend?
                if (Keyboard.IsKeyPressed(KeyCode.Shift))
                {
                    _originalSelection = TextRange.Union(_owner.Selection, _originalSelection);
                }

                // Set new selection
                _owner.SetSelection(_originalSelection);
            }

            public override void Drag(Vector pt)
            {
                // Hit test
                var htr = _owner._document.HitTest((float)pt.X, (float)pt.Y);

                // Map selection kind
                var sel = _owner._document.GetSelectionRange(htr.CaretPosition, _selectionKind);

                // Extend original selection with new selection
                _owner.SetSelection(TextRange.Union(_originalSelection, sel));
            }

            public override void Up(Vector pt)
            {
                // No-op
            }

            TextEditorView _owner;
            SelectionKind _selectionKind;
            TextRange _originalSelection;
        }

        // Private members
        bool _showCursor = true;
        CaretView _caretView;
        TextDocument _document;
        TextRange _selection;
        TextRange _pendingSelection;
        Vector _pendingContentOffset;
        float _lastHeight;
        int _trackingPointer = -1;
        float? _ghostXCoord;
        PointerDragHandler _pointerDragHandler;
        bool _overtype = false;
        Color _selectionColor = Color.FromARGB(0xFF9bcaef);
    }
}