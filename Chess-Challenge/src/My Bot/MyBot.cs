//#define LOGGING
//#define VISUALIZER

using ChessChallenge.API;
using System;
using System.Linq;
using System.Collections.Generic;

public class MyBot : IChessBot
{
                   //PieceType[] pieceTypes    = { PieceType.None, PieceType.Pawn, PieceType.Knight, PieceType.Bishop, PieceType.Rook, PieceType.Queen, PieceType.King};
    private readonly       int[] k_pieceValues = {           0,              100,            300,              320,              500,            900,             20000 };

    private const byte INVALID = 0, EXACT = 1, LOWERBOUND = 2, UPPERBOUND = 3;

    //14 bytes per entry, likely will align to 16 bytes due to padding (if it aligns to 32, recalculate max TP table size)
    struct Transposition
    {
        public ulong zobristHash;
        public Move move;
        public int evaluation;
        public sbyte depth;
        public byte flag;
    }; 

    Move[] m_killerMoves;

    Transposition[] m_TPTable;
    ulong k_TpMask = 0x7FFFFF; //4.7 million entries, likely consuming about 151 MB
    sbyte k_maxDepth = 8;
    int k_timefraction = 40;

    Board m_board;

#if LOGGING
    private int m_evals = 0;
    private int m_nodes = 0;
#endif


    public MyBot()
    {
        m_killerMoves = new Move[k_maxDepth * 2];
        m_TPTable = new Transposition[k_TpMask + 1];
    }

    public Move Think(Board board, Timer timer)
    {
        #if VISUALIZER
            BitboardHelper.VisualizeBitboard(GetBoardControl(PieceType.Pawn, !board.IsWhiteToMove));
        #endif
        m_board = board;
        #if LOGGING
        Console.WriteLine(board.GetFenString());
        #endif
        Transposition bestMove = m_TPTable[board.ZobristKey & k_TpMask];
        int maxTime = timer.MillisecondsRemaining/k_timefraction;
        for(sbyte depth = 1; depth <= k_maxDepth; depth++)
        {
            #if LOGGING
                m_evals = 0;
                m_nodes = 0;
            #endif
            Search(depth, int.MinValue, int.MaxValue, board.IsWhiteToMove ? 1 : -1);
            bestMove = m_TPTable[board.ZobristKey & k_TpMask];
            #if LOGGING
                Console.WriteLine("Depth: {0,2} | Nodes: {1,10} | Evals: {2,10} | Time: {3,5} Milliseconds | Best {4} | Eval: {5}", depth, m_nodes, m_evals, timer.MillisecondsElapsedThisTurn, bestMove.move, bestMove.evaluation);
            #endif
            if(!ShouldExecuteNextDepth(timer, maxTime)) break;
        }
        #if LOGGING
            Console.Write("PV: ");
        PrintPV(board);
        #endif
        return bestMove.move;
    }

    int Search(int depth, int alpha, int beta, int color)
    {
        #if LOGGING 
        m_nodes++;
        #endif
        
        if(depth <= 0) return QSearch(depth, alpha, beta, color);
        int bestEvaluation = int.MinValue;
        int startingAlpha = alpha;

        ref Transposition transposition = ref m_TPTable[m_board.ZobristKey & k_TpMask];
        if(transposition.zobristHash == m_board.ZobristKey && transposition.flag != INVALID && transposition.depth >= depth)
        {
            if(transposition.flag == EXACT) return transposition.evaluation;
            else if(transposition.flag == LOWERBOUND) alpha = Math.Max(alpha, transposition.evaluation);
            else if(transposition.flag == UPPERBOUND) beta = Math.Min(beta, transposition.evaluation);
            if(alpha >= beta) return transposition.evaluation;
        }

        var moves = m_board.GetLegalMoves();

        if(m_board.IsDraw()) return -10;
        if(m_board.IsInCheckmate()) return int.MinValue + m_board.PlyCount;

        OrderMoves(ref moves, depth);
 
        Move bestMove = moves[0];

        foreach(Move m in moves)
        {
            m_board.MakeMove(m);
            int evaluation = -Search((sbyte)(depth - 1), -beta, -alpha, -color);
            m_board.UndoMove(m);

            if(bestEvaluation < evaluation)
            {
                bestEvaluation = evaluation;
                bestMove = m;
            }

            alpha = Math.Max(alpha, bestEvaluation);
            if(alpha >= beta) break;
        }

        //after finding best move
        transposition.evaluation = bestEvaluation;
        transposition.zobristHash = m_board.ZobristKey;
        transposition.move = bestMove;
        if(bestEvaluation < startingAlpha) 
            transposition.flag = UPPERBOUND;
        else if(bestEvaluation >= beta) 
        {
            transposition.flag = LOWERBOUND;
            if(!bestMove.IsCapture) 
                m_killerMoves[depth] = bestMove;
        }
        else transposition.flag = EXACT;
        transposition.depth = (sbyte)depth;

        return bestEvaluation;
    }

    int QSearch(int depth, int alpha, int beta, int color)
    {
        #if LOGGING
        m_nodes++;
        #endif
        
        Move[] moves;
        if(m_board.IsInCheck()) moves = m_board.GetLegalMoves();
        else
        {
            moves = m_board.GetLegalMoves(true);
            if(m_board.IsInCheckmate()) return int.MinValue + m_board.PlyCount;
            if(moves.Length == 0) return Evaluate(color);
        }

        ref Transposition transposition = ref m_TPTable[m_board.ZobristKey & k_TpMask];
        if(transposition.zobristHash == m_board.ZobristKey && transposition.flag != INVALID && transposition.depth >= 0)
        {
            if(transposition.flag == EXACT) return transposition.evaluation;
            else if(transposition.flag == LOWERBOUND) alpha = Math.Max(alpha, transposition.evaluation);
            else if(transposition.flag == UPPERBOUND) beta = Math.Min(beta, transposition.evaluation);
            if(alpha >= beta) return transposition.evaluation;
        }

        alpha = Math.Max(Evaluate(color), alpha);
        if(alpha >= beta) return beta;

        OrderMoves(ref moves, depth);
 
        foreach(Move m in moves)
        {
            m_board.MakeMove(m);
            int evaluation = -QSearch(depth - 1, -beta, -alpha, -color);
            m_board.UndoMove(m);

            alpha = Math.Max(evaluation, alpha);
            if(alpha >= beta) break;
        }

        return alpha;
    }

    void OrderMoves(ref Move[] moves, int depth)
    {
        List<Tuple<Move, int>> orderedMoves = new();
        foreach(Move m in moves) orderedMoves.Add(new Tuple<Move, int>(m, GetMovePriority(m, depth)));
        orderedMoves.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        for(int i = 0; i < moves.Length; i++) moves[i] = orderedMoves[i].Item1;
    }

    int GetMovePriority(Move move, int depth)
    {
        int priority = 0;
        Transposition tp = m_TPTable[m_board.ZobristKey & k_TpMask];
        if(tp.move == move && tp.zobristHash == m_board.ZobristKey) 
            priority += 1000;
        else if (move.IsCapture) 
            priority =  2 + 10 * ((int)move.CapturePieceType - (int)move.MovePieceType);
        else if (depth >= 0 && move.Equals(m_killerMoves[depth]))
            priority =  1;
        return priority;
    }

    int Evaluate(int color)
    {
        #if LOGGING
        m_evals++;
        #endif
        int materialCount = 0;
        // int PSTscores = 0;
        for (int i = 1; ++i < 7;)
        {
            materialCount += (m_board.GetPieceList((PieceType)i, true).Count - m_board.GetPieceList((PieceType)i, false).Count) * k_pieceValues[i];
        } 
        ulong visibleSquaresBitboard = 0;
        var availableMoves = m_board.GetLegalMoves();
        int mobility = availableMoves.Length;
        foreach(Move m in availableMoves)
        {
            BitboardHelper.SetSquare(ref visibleSquaresBitboard, m.TargetSquare);
            if((int) m.MovePieceType > 1 && (int) m.MovePieceType < 5) mobility++;
            else if(m.IsCastles) mobility += 5;
            else if(m.IsPromotion) mobility += 5;
        }

        return materialCount * color + mobility + BitboardHelper.GetNumberOfSetBits(visibleSquaresBitboard);
    }
/*  PST stuff
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
        sbyte unpackedData = unchecked((sbyte)((PackedEvaluationTables[rank, file] >> 8 * ((int)type - 1)) & 0xFF));

        // Merge the sign back into the original unpacked data
        // by first bitwise-ANDing it in with a sign mask, and then ORing it back into the unpacked data
        //unpackedData = (sbyte)((byte)unpackedData | (0b10000000 & unpackedData));

        // Invert eval scores for black pieces
        return isWhite ? unpackedData : -unpackedData;
    }
*/

    bool ShouldExecuteNextDepth(Timer timer, int maxThinkTime)
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

    

#if VISUALIZER
    ulong GetBoardControl(PieceType pt, bool forWhite)
    {
        ulong uncontrolledBitboard = 0xffffffffffffffff;
        ulong controlledBitboard = 0;
        PieceList whitePieces = m_board.GetPieceList(pt, true);
        PieceList blackPieces = m_board.GetPieceList(pt, false);
        int whitePieceNum = whitePieces.Count;
        int blackPieceNum = blackPieces.Count;
        int maxPieceNum = Math.Max(whitePieceNum, blackPieceNum);
        for(int j = 0; j < maxPieceNum; j++)
        {
            ulong whitePieceBitboard = whitePieceNum > j ? GetAttacks(whitePieces[j].Square, pt,  true) : 0;
            ulong blackPieceBitboard = blackPieceNum > j ? GetAttacks(blackPieces[j].Square, pt, false) : 0;
            uncontrolledBitboard &= ~(whitePieceBitboard | blackPieceBitboard);
            controlledBitboard |= whitePieceBitboard;
            controlledBitboard &= ~blackPieceBitboard;
        }
        return forWhite ? controlledBitboard : ~(controlledBitboard ^ uncontrolledBitboard);
    }
    ulong GetAttacks(Square square, PieceType pt, bool isWhite)
    {
        return pt switch
        {
            PieceType.Pawn => BitboardHelper.GetPawnAttacks(square, isWhite),
            PieceType.Knight => BitboardHelper.GetKnightAttacks(square),
            PieceType.King => BitboardHelper.GetKingAttacks(square),
            _ => BitboardHelper.GetSliderAttacks(pt, square, m_board),
        };
    }
#endif
}