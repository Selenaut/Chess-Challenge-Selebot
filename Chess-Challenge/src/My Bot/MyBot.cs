//#define LOGGING

using ChessChallenge.API;
using System;
using System.Linq;
using System.Collections.Generic;

public class MyBot : IChessBot
{
    private readonly int[] k_pieceValues = { 0, 100, 300, 320, 500, 900, 20000 };
    private readonly int[] k_mvvValues = { 0, 10, 20, 30, 40, 50, 0 };
    private readonly int[] k_lvaValues = { 0, 5, 4, 3, 2, 1, 0 };

    private int[,,] PSTables;
    private const byte INVALID = 0, EXACT = 1, LOWERBOUND = 2, UPPERBOUND = 3;

    //14 bytes per entry, likely will align to 16 bytes due to padding (if it aligns to 32, recalculate max TP table size)
    public struct Transposition
    {
        public ulong zobristHash;
        public Move move;
        public int evaluation;
        public sbyte depth;
        public byte flag;
    }; 

    private Transposition[] m_TPTable;
    private ulong k_TpMask = 0x7FFFFF; //4.7 million entries, likely consuming about 151 MB
    private const sbyte k_maxDepth = 8;
    private const int   k_maxTime = 3000;

#if LOGGING
    private int m_evals = 0;
    private int m_nodes = 0;
#endif


    public MyBot()
    {
        PSTables = new int[8,8,8];
        m_TPTable = new Transposition[k_TpMask + 1];
    }

    public Move Think(Board board, Timer timer)
    {
        Console.WriteLine(board.GetFenString());
        Transposition bestMove = m_TPTable[board.ZobristKey & k_TpMask];
        for(sbyte depth = 1; depth <= k_maxDepth; depth++)
        {
            #if LOGGING
                m_evals = 0;
                m_nodes = 0;
            #endif
            Search(board, depth, int.MinValue, int.MaxValue, board.IsWhiteToMove ? 1 : -1);
            bestMove = m_TPTable[board.ZobristKey & k_TpMask];
            #if LOGGING
                Console.WriteLine("Depth: {0,2} | Nodes: {1,10} | Evals: {2,10} | Time: {3,5} Milliseconds | Best {4} | Eval: {5}", depth, m_nodes, m_evals, timer.MillisecondsElapsedThisTurn, bestMove.move, bestMove.evaluation);
            #endif
            if(!ShouldExecuteNextDepth(timer, k_maxTime)) break;
        }
        #if LOGGING
            Console.Write("PV: ");
        PrintPV(board);
        #endif
        return bestMove.move;
    }

    public int Search(Board board, sbyte depth, int alpha, int beta, int color)
    {
        #if LOGGING 
        m_nodes++;
        #endif
        if(depth <= 0) return QSearch(board, alpha, beta, color);
        int bestEvaluation = int.MinValue;
        int startingAlpha = alpha;

        ref Transposition transposition = ref m_TPTable[board.ZobristKey & k_TpMask];
        if(transposition.zobristHash == board.ZobristKey && transposition.flag != INVALID && transposition.depth >= depth)
        {
            if(transposition.flag == EXACT) return transposition.evaluation;
            else if(transposition.flag == LOWERBOUND) alpha = Math.Max(alpha, transposition.evaluation);
            else if(transposition.flag == UPPERBOUND) beta = Math.Min(beta, transposition.evaluation);
            if(alpha >= beta) return transposition.evaluation;
        }

        Move[] moves = board.GetLegalMoves();

        if(board.IsDraw()) return -10;
        if(board.IsInCheckmate()) return int.MinValue + board.PlyCount;

        OrderMoves(ref moves, board, color);
 
        Move bestMove = moves[0];

        foreach(Move m in moves)
        {
            board.MakeMove(m);
            int evaluation = -Search(board, (sbyte)(depth - 1), -beta, -alpha, -color);
            board.UndoMove(m);

            if(bestEvaluation < evaluation)
            {
                bestEvaluation = evaluation;
                bestMove = m;
            }

            alpha = Math.Max(alpha, bestEvaluation);
            if(alpha >= beta) break;
        }

        transposition.evaluation = bestEvaluation;

        transposition.zobristHash = board.ZobristKey;
        transposition.move = bestMove;
        if(bestEvaluation < startingAlpha) transposition.flag = UPPERBOUND;
        else if(bestEvaluation >= beta) transposition.flag = LOWERBOUND;
        else transposition.flag = EXACT;
        transposition.depth = depth;

        return bestEvaluation;
    }

    int QSearch(Board board, int alpha, int beta, int color)
    {
        #if LOGGING
        m_nodes++;
        #endif
        Move[] moves;
        if(board.IsInCheck()) moves = board.GetLegalMoves();
        else
        {
            moves = board.GetLegalMoves(true);
            if(board.IsInCheckmate()) return int.MinValue + board.PlyCount;
            if(moves.Length == 0) return Evaluate(board, 0, color);
        }

        Transposition transposition = m_TPTable[board.ZobristKey & k_TpMask];
        if(transposition.zobristHash == board.ZobristKey && transposition.flag != INVALID && transposition.depth >= 0)
        {
            if(transposition.flag == EXACT) return transposition.evaluation;
            else if(transposition.flag == LOWERBOUND) alpha = Math.Max(alpha, transposition.evaluation);
            else if(transposition.flag == UPPERBOUND) beta = Math.Min(beta, transposition.evaluation);
            if(alpha >= beta) return transposition.evaluation;
        }

        alpha = Math.Max(Evaluate(board, moves.Length, color), alpha);
        if(alpha >= beta) return beta;

        OrderMoves(ref moves, board, color);
 
        foreach(Move m in moves)
        {
            board.MakeMove(m);
            int evaluation = -QSearch(board, -beta, -alpha, -color);
            board.UndoMove(m);

            alpha = Math.Max(evaluation, alpha);
            if(alpha >= beta) break;
        }

        return alpha;
    }

    private void OrderMoves(ref Move[] moves, Board board, int color)
    {
        List<Tuple<Move, int>> orderedMoves = new();
        foreach(Move m in moves) orderedMoves.Add(new Tuple<Move, int>(m, GetMovePriority(m, board, color)));
        orderedMoves.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        for(int i = 0; i < moves.Length; i++) moves[i] = orderedMoves[i].Item1;
    }

    private int GetMovePriority(Move move, Board board, int color)
    {
        int priority = 0;
        Transposition tp = m_TPTable[board.ZobristKey & k_TpMask];
        if(tp.move == move && tp.zobristHash == board.ZobristKey) priority += 1000;
        if (move.IsCapture) priority = k_mvvValues[(int)move.CapturePieceType] + k_lvaValues[(int)move.MovePieceType];
        return priority;
    }

    private int Evaluate(Board board, int mobility, int color)
    {
        #if LOGGING
        m_evals++;
        #endif
        int materialCount = 0;
        int PSTscores = 0;
        for (int i = 0; ++i < 7;)
        {
            PieceList white_pl = board.GetPieceList((PieceType)i, true);
            PieceList black_pl = board.GetPieceList((PieceType)i, false);
            materialCount += (white_pl.Count - black_pl.Count) * k_pieceValues[i];
            for(int j = 0; j < 9; j++)
            {
                if(j < white_pl.Count) PSTscores += GetSquareBonus((PieceType)i, true, white_pl[j].Square.File, white_pl[j].Square.Rank);
                if(j < black_pl.Count) PSTscores -= GetSquareBonus((PieceType)i, false, black_pl[j].Square.File, black_pl[j].Square.Rank);
            }
        } 

        return (materialCount + PSTscores) * color; //+ mobility;
    }

    // Big table packed with data from premade piece square tables
    private readonly ulong[,] PackedEvaluationTables = {
        { 58233348458073600, 61037146059233280, 63851895826342400, 66655671952007680 },
        { 63862891026503730, 66665589183147058, 69480338950193202, 226499563094066 },
        { 63862895153701386, 69480338782421002, 5867015520979476,  8670770172137246 },
        { 63862916628537861, 69480338782749957, 8681765288087306,  11485519939245081 },
        { 63872833708024320, 69491333898698752, 8692760404692736,  11496515055522836 },
        { 63884885386256901, 69502350490469883, 5889005753862902,  8703755520970496 },
        { 63636395758376965, 63635334969551882, 21474836490,       1516 },
        { 58006849062751744, 63647386663573504, 63625396431020544, 63614422789579264 }
    };

    private int GetSquareBonus(PieceType type, bool isWhite, int file, int rank)
    {
        // Because arrays are only 4 squares wide, mirror across files
        if (file > 3)
            file = 7 - file;

        // Mirror vertically for white pieces, since piece arrays are flipped vertically
        if (isWhite)
            rank = 7 - rank;

        // First, shift the data so that the correct byte is sitting in the least significant position
        // Then, mask it out
        sbyte unpackedData = (sbyte)((PackedEvaluationTables[rank, file] >> 8 * ((int)type - 1)) & 0xFF);

        // Merge the sign back into the original unpacked data
        // by first bitwise-ANDing it in with a sign mask, and then ORing it back into the unpacked data
        unpackedData = (sbyte)((byte)unpackedData | (0b10000000 & unpackedData));

        // Invert eval scores for black pieces
        return isWhite ? unpackedData : -unpackedData;
    }

    private bool ShouldExecuteNextDepth(Timer timer, int maxThinkTime)
    {
        int currentThinkTime = timer.MillisecondsElapsedThisTurn;
        return ((maxThinkTime - currentThinkTime) > currentThinkTime * 3);
    }

    #if LOGGING
    private void PrintPV(Board board)
    {
        ulong zHash = board.ZobristKey;
        Transposition tp = m_TPTable[zHash & k_TpMask];
        if(tp.flag != INVALID && tp.zobristHash == zHash)
        {
            Console.Write("{0} | ", tp.move);
            board.MakeMove(tp.move);
            PrintPV(board);
        }
        Console.WriteLine("");
        board.UndoMove(tp.move);
    }
    #endif
}