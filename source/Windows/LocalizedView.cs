using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace UpsGuardian
{
    // Keeps canonical captions separate from translated display text. Switching
    // languages never recreates the Form or interrupts the protection process.
    sealed class LocalizedView : IDisposable
    {
        sealed class Binding
        {
            public Control Control;
            public string Source;
            public string AccessibleSource;
            public Font OriginalFont;
            public Font OwnedFont;
            public bool Applying;
        }
        readonly List<Binding> bindings = new List<Binding>();
        readonly List<KeyValuePair<ToolStripItem, string>> menuBindings = new List<KeyValuePair<ToolStripItem, string>>();
        readonly List<KeyValuePair<ColumnHeader, string>> columnBindings = new List<KeyValuePair<ColumnHeader, string>>();
        readonly List<KeyValuePair<Control, string>> accessibilityBindings = new List<KeyValuePair<Control, string>>();
        public void Attach(Control root)
        {
            if (!string.IsNullOrEmpty(root.AccessibleName)) accessibilityBindings.Add(new KeyValuePair<Control, string>(root, root.AccessibleName));
            if (root is Label || root is ButtonBase || root is Form)
            {
                var binding = new Binding { Control = root, Source = root.Text, AccessibleSource = root.AccessibleName, OriginalFont = root.Font };
                bindings.Add(binding);
                root.TextChanged += delegate
                {
                    if (binding.Applying) return;
                    binding.Source = root.Text; Apply(binding);
                };
                Apply(binding);
            }
            var list = root as ListView;
            if (list != null) foreach (ColumnHeader column in list.Columns) columnBindings.Add(new KeyValuePair<ColumnHeader, string>(column, column.Text));
            foreach (Control child in root.Controls) Attach(child);
        }
        public void Attach(ContextMenuStrip menu)
        { if (menu != null) foreach (ToolStripItem item in menu.Items) menuBindings.Add(new KeyValuePair<ToolStripItem, string>(item, item.Text)); }
        void Apply(Binding binding)
        {
            if (binding.Control.IsDisposed) return;
            binding.Applying = true;
            try
            {
                binding.Control.Text = Localization.T(binding.Source);
                if (!string.IsNullOrEmpty(binding.AccessibleSource)) binding.Control.AccessibleName = Localization.T(binding.AccessibleSource);
                // Keep large headings large; fit compact field captions and buttons.
                float size = binding.OriginalFont.Size;
                if (!(binding.Control is Form) && binding.Control.Width > 0 && binding.Control.Height < 65)
                {
                    using (Graphics graphics = binding.Control.CreateGraphics())
                    {
                        int available = binding.Control.Width - (int)((binding.Control is NavButton ? 54 : 8) * graphics.DpiX / 96F);
                        while (size > Math.Min(9F, binding.OriginalFont.Size))
                        {
                            using (var candidate = new Font(binding.OriginalFont.FontFamily, size, binding.OriginalFont.Style))
                                if (TextRenderer.MeasureText(graphics, binding.Control.Text, candidate).Width <= available) break;
                            size -= 0.5F;
                        }
                    }
                }
                if (Math.Abs(binding.Control.Font.Size - size) > 0.01F)
                {
                    Font previous = binding.OwnedFont;
                    binding.OwnedFont = Math.Abs(size - binding.OriginalFont.Size) < 0.01F ? null : new Font(binding.OriginalFont.FontFamily, size, binding.OriginalFont.Style);
                    binding.Control.Font = binding.OwnedFont ?? binding.OriginalFont;
                    if (previous != null) previous.Dispose();
                }
            }
            finally { binding.Applying = false; }
        }
        public void ApplyAll()
        {
            foreach (Binding binding in bindings) Apply(binding);
            foreach (var binding in menuBindings) binding.Key.Text = Localization.T(binding.Value);
            foreach (var binding in columnBindings) binding.Key.Text = Localization.T(binding.Value);
            foreach (var binding in accessibilityBindings) binding.Key.AccessibleName = Localization.T(binding.Value);
        }
        public void Dispose()
        { foreach (Binding binding in bindings) if (binding.OwnedFont != null) binding.OwnedFont.Dispose(); }
    }
}
