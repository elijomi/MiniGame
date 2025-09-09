using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Components;

namespace BrowserFun.Pages;

public partial class Solitaire : ComponentBase
{
    private enum Suit { Clubs, Diamonds, Hearts, Spades }
    private enum DragSourceKind { Tableau, Waste, Foundation }
    private enum SizeMode { Small, Comfortable, Large }

    private readonly record struct Card(Suit Suit, int Rank, bool FaceUp)
    {
        public bool IsRed => Suit is Suit.Hearts or Suit.Diamonds;
    }

    private readonly List<Card> stock = new();
    private readonly List<Card> waste = new();
    private readonly List<Card>[] foundations = new List<Card>[4]
    {
        new(), new(), new(), new()
    };
    private readonly List<Card>[] tableau = new List<Card>[7]
    {
        new(), new(), new(), new(), new(), new(), new()
    };

    private int moves = 0;
    private int redeals = 0; // unlimited, tracked
    private SizeMode sizeMode = SizeMode.Large; // current state is Large
    private string BoardSizeClass => sizeMode switch
    {
        SizeMode.Small => "size-small",
        SizeMode.Comfortable => "size-comfy",
        SizeMode.Large => "size-large",
        _ => "size-large"
    };
    private int FaceUpTableauOffset => sizeMode switch { SizeMode.Small => 28, SizeMode.Comfortable => 34, SizeMode.Large => 42, _ => 34 };
    private int FaceDownTableauOffset => sizeMode switch { SizeMode.Small => 12, SizeMode.Comfortable => 14, SizeMode.Large => 16, _ => 14 };
    private int WasteFanStepPx => sizeMode switch { SizeMode.Small => 14, SizeMode.Comfortable => 16, SizeMode.Large => 20, _ => 16 };

    private void SetSize(SizeMode mode)
    {
        sizeMode = mode;
        StateHasChanged();
    }

    private readonly record struct DragPayload(DragSourceKind Kind, int Index, int StartIndex);
    private DragPayload? selected; // click-to-move selection

    private readonly Random rng = new();
    private bool isGameWon = false;

    protected override void OnInitialized()
    {
        NewGame();
    }

    private void NewGame()
    {
        moves = 0;
        redeals = 0;
        // Reset undo history so Undo is disabled at a fresh start
        undoStack.Clear();
        isGameWon = false;
        stock.Clear(); waste.Clear();
        foreach (var f in foundations) f.Clear();
        foreach (var t in tableau) t.Clear();

        // build and shuffle deck
        var deck = new List<Card>(52);
        foreach (Suit s in Enum.GetValues(typeof(Suit)))
            for (int r = 1; r <= 13; r++) deck.Add(new Card(s, r, false));

        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        // deal tableau 1..7 columns
        int cursor = 0;
        for (int col = 0; col < 7; col++)
        {
            for (int i = 0; i <= col; i++)
            {
                var faceUp = (i == col);
                var c = deck[cursor++];
                tableau[col].Add(c with { FaceUp = faceUp });
            }
        }

        // rest to stock
        for (; cursor < deck.Count; cursor++) stock.Add(deck[cursor]);
    }

    private bool CanUndo => undoStack.Count > 0;
    private readonly Stack<GameSnapshot> undoStack = new();
    private readonly record struct GameSnapshot(
        Card[] Stock, Card[] Waste,
        Card[][] Foundations, Card[][] Tableau,
        int Moves, int Redeals
    );

    private void PushUndo()
    {
        undoStack.Push(new GameSnapshot(
            stock.ToArray(),
            waste.ToArray(),
            foundations.Select(p => p.ToArray()).ToArray(),
            tableau.Select(p => p.ToArray()).ToArray(),
            moves, redeals
        ));
    }

    private void Undo()
    {
        if (!CanUndo) return;
        var s = undoStack.Pop();
        stock.Clear(); stock.AddRange(s.Stock);
        waste.Clear(); waste.AddRange(s.Waste);
        for (int i = 0; i < 4; i++) { foundations[i].Clear(); foundations[i].AddRange(s.Foundations[i]); }
        for (int i = 0; i < 7; i++) { tableau[i].Clear(); tableau[i].AddRange(s.Tableau[i]); }
        moves = s.Moves + 1; // undo counts as a move
        redeals = s.Redeals;
        isGameWon = false;
    }

    private void DrawFromStock()
    {
        PushUndo();
        if (stock.Count > 0)
        {
            var c = stock[^1]; stock.RemoveAt(stock.Count - 1);
            waste.Add(c with { FaceUp = true });
            moves++;
        }
        else if (waste.Count > 0)
        {
            // redeal: move waste back to stock face-down, preserving order reversed
            for (int i = waste.Count - 1; i >= 0; i--)
            {
                var c = waste[i];
                stock.Add(c with { FaceUp = false });
            }
            waste.Clear();
            redeals++; moves++;
        }
        // Clear any selection after drawing or redealing
        selected = null;
    }

    private void OnClickWaste()
    {
        if (waste.Count == 0) return;
        var topIdx = waste.Count - 1;
        if (selected is DragPayload s && s.Kind == DragSourceKind.Waste && s.Index == 0 && s.StartIndex == topIdx)
        {
            // Toggle off if clicking the already-selected waste card
            selected = null;
            return;
        }
        selected = new DragPayload(DragSourceKind.Waste, 0, topIdx);
    }

    private void OnClickFoundation(int f)
    {
        if (selected is DragPayload s)
        {
            switch (s.Kind)
            {
                case DragSourceKind.Waste:
                {
                    if (waste.Count == 0) break;
                    var card = waste[^1];
                    LogAttemptToFoundation(card, f);
                    TryMoveSingleToFoundation(card, () => { waste.RemoveAt(waste.Count - 1); return true; }, f);
                    break;
                }
                case DragSourceKind.Tableau:
                {
                    var t = s.Index;
                    var topIdx = FindTopFaceUpIndex(t);
                    if (topIdx < 0) break;
                    var card = tableau[t][topIdx];
                    LogAttemptToFoundation(card, f);
                    TryMoveSingleToFoundation(card, () => { tableau[t].RemoveAt(topIdx); AutoFlipTop(t); return true; }, f);
                    break;
                }
            }
            selected = null;
        }
        else
        {
            if (foundations[f].Count > 0)
                selected = new DragPayload(DragSourceKind.Foundation, f, foundations[f].Count - 1);
        }
    }

    private void OnClickTableau(int t)
    {
        if (selected is DragPayload s)
        {
            switch (s.Kind)
            {
                case DragSourceKind.Waste:
                {
                    if (waste.Count == 0) break;
                    var card = waste[^1];
                    LogAttemptToTableau(card, t);
                    if (CanPlaceOnTableau(card, t))
                    {
                        PushUndo();
                        waste.RemoveAt(waste.Count - 1);
                        tableau[t].Add(card);
                        moves++;
                    }
                    break;
                }
                case DragSourceKind.Tableau:
                {
                    var from = s.Index; var start = s.StartIndex;
                    if (from == t) break;
                    var run = tableau[from].Skip(start).ToList();
                    if (run.Count == 0 || !run[0].FaceUp) break;
                    {
                        var placeOk = CanPlaceOnTableau(run[0], t);
                        var runOk = IsValidRun(run);
                        LogAttemptToTableau(run[0], t, placeOk && runOk);
                    }
                    if (CanPlaceOnTableau(run[0], t) && IsValidRun(run))
                    {
                        PushUndo();
                        tableau[from].RemoveRange(start, tableau[from].Count - start);
                        tableau[t].AddRange(run);
                        AutoFlipTop(from);
                        moves++;
                    }
                    break;
                }
                case DragSourceKind.Foundation:
                {
                    var f = s.Index;
                    if (foundations[f].Count == 0) break;
                    var card = foundations[f][^1];
                    LogAttemptToTableau(card, t);
                    if (CanPlaceOnTableau(card, t))
                    {
                        PushUndo();
                        foundations[f].RemoveAt(foundations[f].Count - 1);
                        tableau[t].Add(card);
                        moves++;
                    }
                    break;
                }
            }
            selected = null;
        }
        else
        {
            // Select the topmost face-up card in the pile (start of run)
            var i = FindTopFaceUpIndex(t);
            if (i >= 0) selected = new DragPayload(DragSourceKind.Tableau, t, i);
        }
    }

    // Handle clicks on individual tableau cards to allow selecting any face-up card
    private void OnClickTableauCard(int t, int i)
    {
        // If there's an active selection from another pile, treat this as a drop onto pile t
        if (selected is DragPayload s)
        {
            // If selection is not from this same tableau pile, attempt the move to this pile
            if (s.Kind != DragSourceKind.Tableau || s.Index != t)
            {
                OnClickTableau(t);
                return;
            }

            // Selection is from this same pile: update the run start to the clicked face-up card
            if (i >= 0 && i < tableau[t].Count && tableau[t][i].FaceUp)
            {
                // Toggle off if re-clicking the same selected card
                if (s.StartIndex == i)
                {
                    selected = null;
                }
                else
                {
                    selected = new DragPayload(DragSourceKind.Tableau, t, i);
                }
            }
            return;
        }

        // No selection yet: select clicked card if it's face-up
        if (i >= 0 && i < tableau[t].Count && tableau[t][i].FaceUp)
        {
            selected = new DragPayload(DragSourceKind.Tableau, t, i);
        }
    }

    // Double-click: attempt to auto-move the top face-up card to its foundation
    private void OnDoubleClickTableauCard(int t, int i)
    {
        if (t < 0 || t >= tableau.Length) return;
        if (tableau[t].Count == 0) return;
        var topIndex = tableau[t].Count - 1;
        if (i != topIndex) return; // Only allow topmost card to auto-move
        var card = tableau[t][topIndex];
        if (!card.FaceUp) return;

        var f = FindFoundationIndexFor(card);
        if (f is int fi)
        {
            LogAttemptToFoundation(card, fi);
            TryMoveSingleToFoundation(card, () => { tableau[t].RemoveAt(topIndex); AutoFlipTop(t); return true; }, fi);
            selected = null; // clear any selection that might have been started by single-click
        }
    }

    private int FindTopFaceUpIndex(int t)
    {
        for (int i = tableau[t].Count - 1; i >= 0; i--)
        {
            if (tableau[t][i].FaceUp) return i;
        }
        return -1;
    }

    private int? FindFoundationIndexFor(Card card)
    {
        // Prefer an existing pile with the same suit if possible
        for (int f = 0; f < 4; f++)
        {
            if (foundations[f].Count > 0 && foundations[f][^1].Suit == card.Suit && CanPlaceOnFoundation(card, f))
                return f;
        }
        // Otherwise, any pile that legally accepts (e.g., Ace to empty)
        for (int f = 0; f < 4; f++)
        {
            if (CanPlaceOnFoundation(card, f)) return f;
        }
        return null;
    }

    // Double-click top waste card to auto-move to a foundation if legal
    private void OnDoubleClickWaste()
    {
        if (waste.Count == 0) return;
        var card = waste[^1];
        var f = FindFoundationIndexFor(card);
        if (f is int fi)
        {
            LogAttemptToFoundation(card, fi);
            TryMoveSingleToFoundation(card, () => { waste.RemoveAt(waste.Count - 1); return true; }, fi);
            selected = null;
        }
    }

    private string CardLabel(Card c) => $"{RankToText(c.Rank)}{SuitToUtf16(c.Suit)}";

    private void LogAttemptToTableau(Card moving, int t, bool? legalOverride = null)
    {
        var pile = tableau[t];
        var target = pile.Count == 0 ? "empty" : CardLabel(pile[^1]);
        var legal = legalOverride ?? CanPlaceOnTableau(moving, t);
        Console.WriteLine($"Attempt: {CardLabel(moving)} -> {target}: {(legal ? "Legal" : "Illegal")}");
    }

    private void LogAttemptToFoundation(Card moving, int f)
    {
        var pile = foundations[f];
        var target = pile.Count == 0 ? "empty" : CardLabel(pile[^1]);
        var legal = CanPlaceOnFoundation(moving, f);
        Console.WriteLine($"Attempt: {CardLabel(moving)} -> {target}: {(legal ? "Legal" : "Illegal")}");
    }

    private void TryMoveSingleToFoundation(Card card, Func<bool> removeFromSource, int foundationIndex)
    {
        if (CanPlaceOnFoundation(card, foundationIndex))
        {
            PushUndo();
            removeFromSource();
            foundations[foundationIndex].Add(card);
            moves++;
            CheckWin();
        }
    }

    private void CheckWin()
    {
        // Win when each foundation pile has 13 cards (A..K)
        if (foundations.All(p => p.Count == 13))
        {
            isGameWon = true;
        }
    }

    private bool CanPlaceOnFoundation(Card card, int f)
    {
        var pile = foundations[f];
        if (pile.Count == 0) return card.Rank == 1; // Ace
        var top = pile[^1];
        return card.Suit == top.Suit && card.Rank == top.Rank + 1;
    }

    private bool CanPlaceOnTableau(Card card, int t)
    {
        var pile = tableau[t];
        if (pile.Count == 0) return card.Rank == 13; // King on empty
        var top = pile[^1];
        if (!top.FaceUp) return false;
        return (card.IsRed != top.IsRed) && (card.Rank == top.Rank - 1);
    }

    private static bool IsValidRun(List<Card> run)
    {
        for (int i = 0; i < run.Count - 1; i++)
        {
            var a = run[i]; var b = run[i + 1];
            if (!b.FaceUp) return false;
            if ((a.IsRed == b.IsRed) || (a.Rank != b.Rank + 1)) return false;
        }
        return true;
    }

    private void AutoFlipTop(int t)
    {
        if (tableau[t].Count == 0) return;
        var top = tableau[t][^1];
        if (!top.FaceUp)
        {
            tableau[t][^1] = top with { FaceUp = true };
        }
    }

    private static IEnumerable<(Card card, int index)> EnumeratePile(List<Card> pile)
    {
        for (int i = 0; i < pile.Count; i++) yield return (pile[i], i);
    }

    private static string RankToText(int r) => r switch
    {
        1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => r.ToString()
    };

    private static string SuitToGlyph(Suit s) => s switch
    {
        Suit.Clubs => "�T�",
        Suit.Diamonds => "�T�",
        Suit.Hearts => "�T�",
        Suit.Spades => "�T�",
        _ => "?"
    };

    private bool IsSelected(DragSourceKind kind, int index, int cardIndex)
        => selected is DragPayload s && s.Kind == kind && s.Index == index && s.StartIndex == cardIndex;

    private static string SuitToUtf16(Suit s)
    {
        return s switch
        {
            Suit.Clubs => ((char)0x2663).ToString(),
            Suit.Diamonds => ((char)0x2666).ToString(),
            Suit.Hearts => ((char)0x2665).ToString(),
            Suit.Spades => ((char)0x2660).ToString(),
            _ => "?"
        };
    }
}
