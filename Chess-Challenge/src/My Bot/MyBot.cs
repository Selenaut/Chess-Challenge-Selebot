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
        #if LOGGING
            if(board.GameMoveHistory.Length > 0) Console.WriteLine("Opponent played {0}", board.GameMoveHistory.Last().ToString());
        #endif
        
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
            Search(depth, -100000000, 100000000, board.IsWhiteToMove ? 1 : -1);
            bestMove = m_TPTable[board.ZobristKey & k_TpMask];
            #if LOGGING
                Console.WriteLine("Depth: {0,2} | Nodes: {1,10} | Evals: {2,10} | Time: {3,5} Milliseconds | Best {4} | Eval: {5}", depth, m_nodes, m_evals, timer.MillisecondsElapsedThisTurn, bestMove.move, bestMove.evaluation);
            #endif
            if(!ShouldExecuteNextDepth(timer, maxTime)) break;
        }
        #if LOGGING
            Console.Write("PV: ");
        PrintPV(board, 20);
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
            else if(transposition.flag == LOWERBOUND && transposition.evaluation >= beta)  return transposition.evaluation;
            else if(transposition.flag == UPPERBOUND && transposition.evaluation <= alpha) return transposition.evaluation;
            if(alpha >= beta) return transposition.evaluation;
        }

        var moves = m_board.GetLegalMoves();

        if(m_board.IsDraw()) return -10;
        if(m_board.IsInCheckmate()) return m_board.PlyCount - 100000000;

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
            if(m_board.IsInCheckmate()) return -100000000 + m_board.PlyCount;
            if(moves.Length == 0) return Evaluate(color);
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
        int[] movePriorities = new int[moves.Length];
        for(int m = 0; m < moves.Length; m++) movePriorities[m] =  GetMovePriority(moves[m], depth);
        Array.Sort(movePriorities, moves);
        Array.Reverse(moves);
    }

    int GetMovePriority(Move move, int depth)
    {
        int priority = 0;
        Transposition tp = m_TPTable[m_board.ZobristKey & k_TpMask];
        if(tp.move == move && tp.zobristHash == m_board.ZobristKey) 
            priority += 100000;
        else if (move.IsCapture) 
            priority =  1000 + 10 * (int)move.CapturePieceType - (int)move.MovePieceType;
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
        for (int i = 0; ++i < 7;)
        {
            materialCount += (m_board.GetPieceList((PieceType)i, true).Count - m_board.GetPieceList((PieceType)i, false).Count) * k_pieceValues[i];
        } 
        var availableMoves = m_board.GetLegalMoves();
        int mobility = availableMoves.Length;
        return materialCount * color + mobility;
    }

    bool ShouldExecuteNextDepth(Timer timer, int maxThinkTime)
    {
        int currentThinkTime = timer.MillisecondsElapsedThisTurn;
        return ((maxThinkTime - currentThinkTime) > currentThinkTime * 3);
    }



#if LOGGING
private void PrintPV(Board board, int depth)
{
    ulong zHash = board.ZobristKey;
    Transposition tp = m_TPTable[zHash & k_TpMask];
    if(tp.flag != INVALID && tp.zobristHash == zHash && depth >= 0)
    {
        Console.Write("{0} | ", tp.move);
        board.MakeMove(tp.move);
        PrintPV(board, depth - 1);
    }
    Console.WriteLine("");
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