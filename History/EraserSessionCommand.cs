using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace TraderPen.History
{
    // Agrupa TODAS as remoções/adições feitas durante um único gesto de
    // arrastar a borracha (do MouseDown até o MouseUp) em um único passo
    // de Undo/Redo — em vez de um Ctrl+Z por "mordida" de pixel.
    //
    // Uso:
    //   var session = new EraserSessionCommand(canvas);
    //   session.Remove(elemento);   // aplica na hora (some da tela)
    //   session.Add(elementoNovo);  // aplica na hora (aparece na tela)
    //   ... (repete durante o arraste, sem re-registrar no UndoManager) ...
    //   _undoManager.RegisterCompletedCommand(session); // só ao soltar o mouse
    public class EraserSessionCommand : IUndoableCommand
    {
        private readonly Canvas _canvas;

        // Ordem cronológica de tudo que aconteceu na sessão, para poder
        // desfazer/refazer exatamente na sequência inversa/direta.
        private readonly List<(bool isAdd, UIElement element)> _steps = new();

        public EraserSessionCommand(Canvas canvas)
        {
            _canvas = canvas;
        }

        public bool HasChanges => _steps.Count > 0;

        // Remove o elemento do Canvas AGORA MESMO (feedback instantâneo enquanto
        // arrasta) e registra o passo para o histórico da sessão.
        public void Remove(UIElement element)
        {
            if (_canvas.Children.Contains(element))
            {
                _canvas.Children.Remove(element);
            }
            _steps.Add((isAdd: false, element));
        }

        // Adiciona o elemento (ex.: pedaço restante após o corte) AGORA MESMO
        // e registra o passo para o histórico da sessão.
        public void Add(UIElement element)
        {
            if (!_canvas.Children.Contains(element))
            {
                _canvas.Children.Add(element);
            }
            _steps.Add((isAdd: true, element));
        }

        // Chamado pelo UndoManager só em um REDO (refazer a sessão inteira
        // depois de um Undo). Na primeira vez a sessão já foi aplicada
        // "ao vivo" via Remove/Add acima, então não faz nada de novo aqui.
        public void Execute()
        {
            foreach (var (isAdd, element) in _steps)
            {
                if (isAdd)
                {
                    if (!_canvas.Children.Contains(element))
                        _canvas.Children.Add(element);
                }
                else
                {
                    if (_canvas.Children.Contains(element))
                        _canvas.Children.Remove(element);
                }
            }
        }

        // Desfaz a sessão inteira de uma vez, na ordem inversa.
        public void Undo()
        {
            for (int i = _steps.Count - 1; i >= 0; i--)
            {
                var (isAdd, element) = _steps[i];
                if (isAdd)
                {
                    if (_canvas.Children.Contains(element))
                        _canvas.Children.Remove(element);
                }
                else
                {
                    if (!_canvas.Children.Contains(element))
                        _canvas.Children.Add(element);
                }
            }
        }
    }
}