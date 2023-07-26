//#define DEBUGGING
using ChessChallenge.API;
using System;
using System.Linq;
using System.Collections.Generic;

public class MyBot : IChessBot
{
#if DEBUGGING
    private int d_numPositions;
#endif

    private readonly int[] k_pieceValues = { 0, 100, 300, 310, 500, 900, 20000 };
    private readonly int[] k_mvvValues = { 0, 10, 20, 30, 40, 50, 0 };
    private readonly int[] k_lvaValues = { 0, 5, 4, 3, 2, 1, 0 };

    public struct Transposition
    {
        public Transposition(ulong zHash, int eval, Move m, byte d)
        {
            zobristHash = zHash;
            evaluation = eval;
            move = m;
            depth = d;
        }

        public ulong zobristHash = 0;
        public int evaluation = 0;
        public Move move = Move.NullMove;
        public byte depth = 0;
    };

    private Transposition[] m_TPTable;

    private ulong k_TpMask = 0x1;
    private int k_maxDepth = 4;
    private int k_endgameDepth = 2;
    private int k_primaryMoves = 4;
    private int k_primaryMoveBonusDepth = 1;

    private Move m_bestMove;

    public MyBot()
    {
        m_TPTable = new Transposition[k_TpMask + 1];
    }

    public Move Think(Board board, Timer timer)
    {
#if DEBUGGING
        d_numPositions = 0;
        Console.WriteLine(Search(board, 0, k_maxDepth, int.MinValue, int.MaxValue, board.IsWhiteToMove ? 1 : -1, false));
#else
        Search(board, 0, k_maxDepth, int.MinValue, int.MaxValue, board.IsWhiteToMove ? 1 : -1, false);
#endif
#if DEBUGGING
        Console.WriteLine("Positions Evaluated: " + d_numPositions);
#endif

        return m_bestMove;
    }

    //alpha = min score White expects
    //beta  = max score Black expects
    //color: 1 = white, -1 = black
    private int Search(Board board, int depth, int maxDepth, int alpha, int beta, int color, bool isEndgame)
    {
        Move[] allLegalmoves = board.GetLegalMoves();
        Move[] allAttackMoves = board.GetLegalMoves(true);

        //Check whether we're in an endgame position
        if (!isEndgame) isEndgame = IsEndgame(board);

        //check for quiescence search
        bool qSearch = (depth >= maxDepth) && (allAttackMoves.Length != 0);

        //Check for leaf node conditions
        if (board.IsDraw()) return 0;
        if (board.IsInCheckmate()) return -((int.MaxValue / 2) - depth);

        //Check Transposition Table
        
        #region TPTABLE
        ulong zHash = board.ZobristKey;
        ulong zIndex = zHash & k_TpMask;
        bool addToTpTable = false;
        if 
        (
            m_TPTable[zIndex].zobristHash == zHash &&        //is the same board state
            m_TPTable[zIndex].depth >= depth &&              //deeper than current depth
            m_TPTable[zIndex].move != Move.NullMove &&       //not a null move (???)
            allLegalmoves.Contains(m_TPTable[zIndex].move)  //actually legal (also ????)
        )
        {
            if (depth == 0) m_bestMove = m_TPTable[zIndex].move;
            return m_TPTable[zIndex].evaluation;
        }
        else
        {
            addToTpTable = true;
            m_TPTable[zIndex].zobristHash = zHash;
            m_TPTable[zIndex].depth = (byte)depth;
        }
        #endregion
        
        int recordEval = int.MinValue;

        #region QSEARCH_STANDINGPAT
        if(qSearch)
        {
            recordEval = Evaluate(board, isEndgame) * color;
            alpha = Math.Max(recordEval, alpha);
            if(alpha >= beta) return beta;
        }
        #endregion

        if (allLegalmoves.Length == 0 || (depth >= maxDepth && !qSearch)) return Evaluate(board, isEndgame) * color;

        Move[] moves = qSearch ? allAttackMoves : allLegalmoves;

        #region MOVEORDERING
        List<Tuple<Move, int>> orderedMoves = new();
        foreach(Move m in moves) orderedMoves.Add(new Tuple<Move, int>(m, OrderMove(m, board, isEndgame)));
        orderedMoves.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        #endregion

        #region MAXDEPTHCALC
        int newMaxDepth = k_maxDepth; //default
        if (isEndgame) newMaxDepth += k_endgameDepth; //add depth if in endgame
        newMaxDepth += k_primaryMoveBonusDepth; //add primary search bonus
        #endregion
        int i = k_primaryMoves;

        #region SEARCH
        foreach (Tuple<Move, int> move in orderedMoves)
        {
            Move m = move.Item1;
            if(i-- == 0) newMaxDepth -= k_primaryMoveBonusDepth; //when no longer in primary moves, disable bonus depth
            board.MakeMove(m);
            int eval = -Search(board, depth + 1, newMaxDepth, -beta, -alpha, -color, isEndgame);
            board.UndoMove(m);
            if (recordEval < eval)
            {
                recordEval = eval;
                if (depth == 0) m_bestMove = m;
                
                if (addToTpTable)
                {
                    m_TPTable[zIndex].evaluation = recordEval;
                    m_TPTable[zIndex].move = m;
                }
                
            }
            alpha = Math.Max(alpha, eval);
            if (alpha >= beta) break;
        }
        #endregion
        return recordEval;
    }

    private int Evaluate(Board board, bool isEndgame)
    {
        int materialCount = 0;
        int positionalScore = 0;

        for (int i = 0; ++i < 7;)
        {
            materialCount += (board.GetPieceList((PieceType)i, true).Count - board.GetPieceList((PieceType)i, false).Count) * k_pieceValues[i];
        }
        return materialCount + positionalScore;
    }
    private int OrderMove(Move move, Board board, bool isEndgame)
    {
        int priority = 0;
        //PUSH THE PAWN
        if(isEndgame && move.MovePieceType == PieceType.Pawn) priority += 30;
        //prioritize captures
        if (move.IsCapture) priority += MVVLVA((int)move.CapturePieceType, (int)move.MovePieceType);
        return priority;
    }
    
    private bool IsEndgame(Board board)
    {
        return (BitboardHelper.GetNumberOfSetBits(board.AllPiecesBitboard) <= 10);
    }
    private int MVVLVA(int moveType, int captureType)
    {
        return k_mvvValues[captureType] + k_lvaValues[moveType];
    }
}