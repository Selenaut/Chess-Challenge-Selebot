#define DEBUGGING
using ChessChallenge.API;
using System;
using System.Linq;
using System.Collections.Generic;

public class MyBot : IChessBot
{
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

    private ulong k_TpMask = 0xFFFFFF;
    private int k_maxDepth = 5;
    private int k_endgameDepth = 3;

    private int k_endgamePieces = 6;
    private int k_primaryMoveBonusDepth = 2;

    private Move m_bestMove;

    public MyBot()
    {
        m_TPTable = new Transposition[k_TpMask + 1];
    }

    public Move Think(Board board, Timer timer)
    {
        if(board.GetLegalMoves().Length == 1) return board.GetLegalMoves()[0];
        Transposition defaultTP = m_TPTable[board.ZobristKey & k_TpMask];
        m_bestMove = (defaultTP.zobristHash == board.ZobristKey) ? defaultTP.move : Move.NullMove;
        int eval = Search(board, 0, k_maxDepth, int.MinValue, int.MaxValue, board.IsWhiteToMove ? 1 : -1, false);
#if DEBUGGING
        Console.WriteLine(eval));
        Console.WriteLine(PrintPV(board.ZobristKey, board, 10, ""));
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
        if (board.IsDraw()) return -10;
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
        #endregion
        int i = k_primaryMoveBonusDepth;

        #region SEARCH
        foreach (Tuple<Move, int> move in orderedMoves)
        {
            Move m = move.Item1;
            i = Math.Max(i - 1, 0); 
            newMaxDepth += i; //when no longer in primary moves, disable bonus depth
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
            PieceList whitePieces = board.GetPieceList((PieceType)i, true);
            PieceList blackPieces = board.GetPieceList((PieceType)i, false);
            materialCount += (whitePieces.Count - blackPieces.Count) * k_pieceValues[i];
            positionalScore += PieceEval(whitePieces, i, true, isEndgame);
            positionalScore += PieceEval(blackPieces, i, false, isEndgame);
        }

        return materialCount + positionalScore;
    }
    private int OrderMove(Move move, Board board, bool isEndgame)
    {
        int priority = 0;
        Transposition tp = m_TPTable[board.ZobristKey & k_TpMask];
        if(tp.move == move && tp.zobristHash == board.ZobristKey) priority += 1000;
        //PUSH THE PAWN
        if(isEndgame && move.MovePieceType == PieceType.Pawn) priority += 30;
        //prioritize captures
        if (move.IsCapture) priority += MVVLVA((int)move.CapturePieceType, (int)move.MovePieceType);
        return priority;
    }
    
    private bool IsEndgame(Board board)
    {
        return (BitboardHelper.GetNumberOfSetBits(board.AllPiecesBitboard) <= k_endgamePieces);
    }
    private int MVVLVA(int moveType, int captureType)
    {
        return k_mvvValues[captureType] + k_lvaValues[moveType];
    }

    private int PieceEval(PieceList pieces, int type, bool isWhite, bool isEndgame)
    {
        if(type == 1) return PawnEval(pieces, isWhite);
        if(type == 4) return RookEval(pieces, isWhite);
        //if(type == 6) return KingEval(pieces[0], isWhite, isEndgame);
        return CentralizationEval(pieces, isWhite);
    }

    private int KingEval(Piece king, bool isWhite, bool isEndgame)
    {
        ulong bitboard = 0;
        BitboardHelper.SetSquare(ref bitboard, king.Square);
        if(!isEndgame && (bitboard & (isWhite ? 0xc7 : 0xc700000000000000)) != 0) return isWhite ? 50 : -50;
        else return 0;
    }
    private int PawnEval(PieceList pawns, bool isWhite)
    {
        int eval = 0;
        foreach(Piece pawn in pawns)
        {
            ulong bitboard = 0;
            BitboardHelper.SetSquare(ref bitboard, pawn.Square);
            int rank = isWhite ? pawn.Square.Rank : 7 - pawn.Square.Rank;
            eval += 15 * (rank - 1);
            if((bitboard & (isWhite ? 0xffff7e3c1c1c0000 : 0x1c1c3c7effff)) != 0) eval += 10;
        }
        return isWhite ? eval : -eval;
    }

    private int RookEval(PieceList rooks, bool isWhite)
    {
        int eval = 0;
        foreach(Piece rook in rooks)
        {
            int rank = isWhite ? rook.Square.Rank : 7 - rook.Square.Rank;
            int file = rook.Square.File;
            if(rank == 6) eval += 100;
            else if(file > 1) eval += 50;
        }
        return isWhite ? eval : -eval;
    }

    private int CentralizationEval(PieceList pieces, bool isWhite)
    {
        int eval = 0;
        foreach(Piece piece in pieces)
        {
            int rank = Math.Min(piece.Square.Rank, 7 - piece.Square.Rank);
            int file = Math.Min(piece.Square.File, 7 - piece.Square.File);
            eval += 35 * (Math.Min(rank, file) - 2);
        }
        return isWhite ? eval : -eval;
    }

    #if DEBUGGING

    private string PrintPV(ulong zHash, Board board, int maxPlys, string pvString)
    {
        Transposition tp = m_TPTable[zHash & k_TpMask];
        if(tp.zobristHash == zHash && maxPlys > 0)
        {
            board.MakeMove(tp.move);
            zHash = board.ZobristKey;
            pvString += tp.move + " - " + PrintPV(zHash, board, maxPlys - 1, pvString);
            board.UndoMove(tp.move);
            tp = m_TPTable[zHash & k_TpMask];
        }
        return pvString;
    }

    #endif 
}