// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ICSharpCode.AvalonEdit.Editing
{
	class ImeSupport
	{
		readonly TextArea textArea;
		readonly ImeCompositionLayer compositionLayer;
		IntPtr currentContext;
		IntPtr previousContext;
		IntPtr defaultImeWnd;
		HwndSource hwndSource;
		EventHandler requerySuggestedHandler; // we need to keep the event handler instance alive because CommandManager.RequerySuggested uses weak references
		bool isReadOnly;
		bool isCompositionActive;
		int compositionStartOffset = -1;
		int compositionLength;
		bool undoGroupOpen;

		public ImeSupport(TextArea textArea)
		{
			if (textArea == null)
				throw new ArgumentNullException("textArea");
			this.textArea = textArea;
			compositionLayer = new ImeCompositionLayer(textArea);
			textArea.TextView.InsertLayer(compositionLayer, KnownLayer.Caret, LayerInsertionPosition.Below);
			InputMethod.SetIsInputMethodSuspended(this.textArea, textArea.Options.EnableImeSupport);
			// We listen to CommandManager.RequerySuggested for both caret offset changes and changes to the set of read-only sections.
			// This is because there's no dedicated event for read-only section changes; but RequerySuggested needs to be raised anyways
			// to invalidate the Paste command.
			requerySuggestedHandler = OnRequerySuggested;
			CommandManager.RequerySuggested += requerySuggestedHandler;
			textArea.OptionChanged += TextAreaOptionChanged;
		}

		void OnRequerySuggested(object sender, EventArgs e)
		{
			UpdateImeEnabled();
		}

		void TextAreaOptionChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "EnableImeSupport") {
				InputMethod.SetIsInputMethodSuspended(this.textArea, textArea.Options.EnableImeSupport);
				UpdateImeEnabled();
			}
		}

		internal bool IsCompositionActive {
			get { return isCompositionActive; }
		}

		internal int CompositionStartOffset {
			get { return compositionStartOffset; }
		}

		internal int CompositionLength {
			get { return compositionLength; }
		}

		public void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
		{
			UpdateImeEnabled();
		}

		public void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
		{
			if (e.OldFocus == textArea && currentContext != IntPtr.Zero) {
				if (UseInlineComposition) {
					NotifyImePreservingConversionStatus();
				} else {
					ImeNativeWrapper.NotifyIme(currentContext);
				}
			}
			SetCompositionActive(false);
			ClearContext();
		}

		void NotifyImePreservingConversionStatus()
		{
			int conversion;
			int sentence;
			bool hasStatus = ImeNativeWrapper.GetConversionStatus(currentContext, out conversion, out sentence);
			ImeNativeWrapper.NotifyIme(currentContext);
			if (hasStatus)
				ImeNativeWrapper.SetConversionStatus(currentContext, conversion, sentence);
		}

		void UpdateImeEnabled()
		{
			if (textArea.Options.EnableImeSupport && textArea.IsKeyboardFocused) {
				bool newReadOnly = !textArea.ReadOnlySectionProvider.CanInsert(textArea.Caret.Offset);
				if (hwndSource == null || isReadOnly != newReadOnly) {
					ClearContext(); // clear existing context (on read-only change)
					isReadOnly = newReadOnly;
					CreateContext();
				}
			} else {
				ClearContext();
			}
		}

		void ClearContext()
		{
			if (hwndSource != null) {
				ImeNativeWrapper.ImmAssociateContext(hwndSource.Handle, previousContext);
				ImeNativeWrapper.ImmReleaseContext(defaultImeWnd, currentContext);
				currentContext = IntPtr.Zero;
				defaultImeWnd = IntPtr.Zero;
				hwndSource.RemoveHook(WndProc);
				hwndSource = null;
			}
		}

		void CreateContext()
		{
			hwndSource = (HwndSource)PresentationSource.FromVisual(this.textArea);
			if (hwndSource != null) {
				if (isReadOnly) {
					defaultImeWnd = IntPtr.Zero;
					currentContext = IntPtr.Zero;
				} else {
					defaultImeWnd = ImeNativeWrapper.ImmGetDefaultIMEWnd(IntPtr.Zero);
					currentContext = ImeNativeWrapper.ImmGetContext(defaultImeWnd);
				}
				previousContext = ImeNativeWrapper.ImmAssociateContext(hwndSource.Handle, currentContext);
				hwndSource.AddHook(WndProc);
				// UpdateCompositionWindow() will be called by the caret becoming visible

				var threadMgr = ImeNativeWrapper.GetTextFrameworkThreadManager();
				if (threadMgr != null) {
					// Even though the docu says passing null is invalid, this seems to help
					// activating the IME on the default input context that is shared with WPF
					threadMgr.SetFocus(IntPtr.Zero);
				}
			}
		}

		IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			switch (msg) {
				case ImeNativeWrapper.WM_INPUTLANGCHANGE:
					// Don't mark the message as handled; other windows
					// might want to handle it as well.

					// If we have a context, recreate it
					if (hwndSource != null) {
						ClearContext();
						CreateContext();
					}
					break;
				case ImeNativeWrapper.WM_IME_STARTCOMPOSITION:
					if (UseInlineComposition) {
						SetCompositionActive(true);
						FinishDocumentComposition();
						handled = true;
					}
					UpdateCompositionWindow();
					break;
				case ImeNativeWrapper.WM_IME_COMPOSITION:
					if (UseInlineComposition && HandleComposition(lParam))
						handled = true;
					else
						UpdateCompositionWindow();
					break;
				case ImeNativeWrapper.WM_IME_ENDCOMPOSITION:
					SetCompositionActive(false);
					if (UseInlineComposition)
						handled = true;
					break;
			}
			return IntPtr.Zero;
		}

		bool UseInlineComposition {
			get {
				CultureInfo inputLanguage = InputLanguageManager.Current.CurrentInputLanguage;
				return inputLanguage != null && string.Equals(inputLanguage.TwoLetterISOLanguageName, "ko", StringComparison.OrdinalIgnoreCase);
			}
		}

		bool HandleComposition(IntPtr lParam)
		{
			if (currentContext == IntPtr.Zero || textArea.Document == null)
				return false;

			ImeCompositionData composition = ImeNativeWrapper.GetCompositionData(currentContext, lParam);
			if (composition == null)
				return false;

			if (!string.IsNullOrEmpty(composition.Result)) {
				CommitDocumentComposition(composition.Result);
			}

			if (composition.HasCompositionString) {
				if (!string.IsNullOrEmpty(composition.Composition)) {
					SetCompositionActive(true);
					UpdateDocumentComposition(composition.Composition, composition.CursorPosition);
				} else {
					CancelDocumentComposition();
				}
			}

			UpdateCompositionWindow();
			return !string.IsNullOrEmpty(composition.Result) || composition.HasCompositionString;
		}

		void SetCompositionActive(bool value)
		{
			if (isCompositionActive != value) {
				isCompositionActive = value;
				textArea.Caret.UpdateIfVisible();
				if (!value)
					FinishDocumentComposition();
			}
		}

		void UpdateDocumentComposition(string text, int caretOffset)
		{
			if (compositionStartOffset < 0) {
				StartUndoGroup();
				compositionStartOffset = GetCompositionStartOffset();
				compositionLength = text.Length;
				textArea.ReplaceSelectionWithText(text);
			} else {
				int oldLength = compositionLength;
				compositionLength = text.Length;
				textArea.Document.Replace(compositionStartOffset, oldLength, text, OffsetChangeMappingType.CharacterReplace);
			}

			int normalizedCaretOffset = NormalizeCompositionCaretOffset(text, caretOffset);
			textArea.Caret.Offset = compositionStartOffset + normalizedCaretOffset;
			textArea.ClearSelection();
			compositionLayer.SetCompositionSegment(compositionStartOffset, compositionLength, normalizedCaretOffset);
		}

		int GetCompositionStartOffset()
		{
			ISegment selectionSegment = textArea.Selection.SurroundingSegment;
			if (selectionSegment != null)
				return selectionSegment.Offset;
			return textArea.Caret.Offset;
		}

		static int NormalizeCompositionCaretOffset(string text, int caretOffset)
		{
			int textLength = text != null ? text.Length : 0;
			if (caretOffset <= 0)
				return textLength;
			return Math.Min(caretOffset, textLength);
		}

		void CommitDocumentComposition(string result)
		{
			if (compositionStartOffset >= 0) {
				textArea.Selection = Selection.Create(textArea, compositionStartOffset, compositionStartOffset + compositionLength);
				ClearDocumentCompositionState();
				textArea.PerformTextInput(result);
				EndUndoGroup();
			} else {
				textArea.PerformTextInput(result);
			}
		}

		void CancelDocumentComposition()
		{
			if (compositionStartOffset >= 0 && textArea.Document != null) {
				textArea.Document.Remove(compositionStartOffset, compositionLength);
				textArea.Caret.Offset = compositionStartOffset;
			}
			ClearDocumentCompositionState();
			EndUndoGroup();
		}

		void FinishDocumentComposition()
		{
			ClearDocumentCompositionState();
			EndUndoGroup();
		}

		void ClearDocumentCompositionState()
		{
			compositionLayer.Clear();
			compositionStartOffset = -1;
			compositionLength = 0;
		}

		void StartUndoGroup()
		{
			if (!undoGroupOpen && textArea.Document != null) {
				textArea.Document.UndoStack.StartUndoGroup();
				undoGroupOpen = true;
			}
		}

		void EndUndoGroup()
		{
			if (undoGroupOpen && textArea.Document != null) {
				textArea.Document.UndoStack.EndUndoGroup();
				undoGroupOpen = false;
			}
		}

		public void UpdateCompositionWindow()
		{
			if (UseInlineComposition && isCompositionActive)
				return;
			if (currentContext != IntPtr.Zero) {
				ImeNativeWrapper.SetCompositionFont(hwndSource, currentContext, textArea);
				ImeNativeWrapper.SetCompositionWindow(hwndSource, currentContext, textArea);
			}
		}
	}
}
