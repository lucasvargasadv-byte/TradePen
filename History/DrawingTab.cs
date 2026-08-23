using System.Collections.Generic;
using System.Windows;

namespace TraderPen.History
{
    /// <summary>
    /// Representa uma "aba" salva: uma tela de desenho congelada durante a aula,
    /// que pode ser reaberta e editada de novo mais tarde (até o app ser fechado).
    /// </summary>
    public class DrawingTab
    {
        public int Number { get; set; }
        public List<UIElement> Elements { get; set; } = new();
        public UndoManager UndoManager { get; set; } = new();
    }
}