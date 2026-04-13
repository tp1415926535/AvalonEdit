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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Editing
{
	sealed class ImeCompositionLayer : Layer
	{
		readonly TextArea textArea;
		int compositionStartOffset = -1;
		int compositionLength;
		int caretOffset;
		readonly DispatcherTimer caretBlinkTimer;
		bool blink;

		public ImeCompositionLayer(TextArea textArea) : base(textArea.TextView, KnownLayer.Caret)
		{
			if (textArea == null)
				throw new ArgumentNullException("textArea");
			this.textArea = textArea;
			this.IsHitTestVisible = false;
			textView.ScrollOffsetChanged += TextViewRedrawRequested;
			textView.VisualLinesChanged += TextViewRedrawRequested;
			caretBlinkTimer = new DispatcherTimer();
			caretBlinkTimer.Tick += CaretBlinkTimerTick;
		}

		public bool HasComposition {
			get { return compositionStartOffset >= 0 && compositionLength > 0; }
		}

		public void SetCompositionSegment(int startOffset, int length, int caretOffset)
		{
			compositionStartOffset = startOffset;
			compositionLength = Math.Max(0, length);
			this.caretOffset = Math.Max(0, Math.Min(caretOffset, compositionLength));
			StartBlinkAnimation();
			InvalidateVisual();
		}

		public void Clear()
		{
			if (HasComposition) {
				compositionStartOffset = -1;
				compositionLength = 0;
				caretOffset = 0;
				StopBlinkAnimation();
				InvalidateVisual();
			}
		}

		void CaretBlinkTimerTick(object sender, EventArgs e)
		{
			blink = !blink;
			InvalidateVisual();
		}

		void StartBlinkAnimation()
		{
			TimeSpan blinkTime = Win32.CaretBlinkTime;
			blink = true;
			if (blinkTime.TotalMilliseconds > 0) {
				caretBlinkTimer.Interval = blinkTime;
				caretBlinkTimer.Start();
			}
		}

		void StopBlinkAnimation()
		{
			caretBlinkTimer.Stop();
		}

		void TextViewRedrawRequested(object sender, EventArgs e)
		{
			if (HasComposition)
				InvalidateVisual();
		}

		Point GetVisualPosition(int offset, VisualYPosition yPosition)
		{
			return textView.GetVisualPosition(new TextViewPosition(textArea.Document.GetLocation(offset)), yPosition) - textView.ScrollOffset;
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);
			if (!HasComposition || textArea.Document == null)
				return;

			Point start;
			Point end;
			Point caretTop;
			Point caretBottom;
			try {
				textView.EnsureVisualLines();
				start = GetVisualPosition(compositionStartOffset, VisualYPosition.TextBottom);
				end = GetVisualPosition(compositionStartOffset + compositionLength, VisualYPosition.TextBottom);
				caretTop = GetVisualPosition(compositionStartOffset + caretOffset, VisualYPosition.TextTop);
				caretBottom = GetVisualPosition(compositionStartOffset + caretOffset, VisualYPosition.TextBottom);
			} catch (InvalidOperationException) {
				return;
			}

			Brush foreground = (Brush)textView.GetValue(TextBlock.ForegroundProperty);
			Pen underlinePen = new Pen(CloneWithOpacity(foreground, 0.45), 0.75);
			double underlineY = start.Y - 1;
			drawingContext.DrawLine(underlinePen, new Point(start.X, underlineY), new Point(end.X, underlineY));

			if (!blink)
				return;

			Pen caretPen = new Pen(foreground, 1);
			drawingContext.DrawLine(caretPen, caretTop, caretBottom);
		}

		static Brush CloneWithOpacity(Brush brush, double opacity)
		{
			if (brush == null)
				return Brushes.Black;

			Brush clone = brush.CloneCurrentValue();
			clone.Opacity *= opacity;
			return clone;
		}
	}
}
