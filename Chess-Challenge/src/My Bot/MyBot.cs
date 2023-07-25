//#define DEBUGGING
using System;
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

    private static readonly ulong[,] kPackedScores =
    {
        {0x31CDE1EBFFEBCE00, 0x31D7D7F5FFF5D800, 0x31E1D7F5FFF5E200, 0x31EBCDFAFFF5E200},
        {0x31E1E1F604F5D80A, 0x13EBD80009FFEC0A, 0x13F5D8000A000014, 0x13FFCE000A00001E},
        {0x31E1E1F5FAF5E232, 0x13F5D80000000032, 0x0013D80500050A32, 0x001DCE05000A0F32},
        {0x31E1E1FAFAF5E205, 0x13F5D80000050505, 0x001DD80500050F0A, 0xEC27CE05000A1419},
        {0x31E1EBFFFAF5E200, 0x13F5E20000000000, 0x001DE205000A0F00, 0xEC27D805000A1414},
        {0x31E1F5F5FAF5E205, 0x13F5EC05000A04FB, 0x0013EC05000A09F6, 0x001DEC05000A0F00},
        {0x31E213F5FAF5D805, 0x13E214000004EC0A, 0x140000050000000A, 0x14000000000004EC},
        {0x31CE13EBFFEBCE00, 0x31E21DF5FFF5D800, 0x31E209F5FFF5E200, 0x31E1FFFB04F5E200},
    };

    private sbyte[,,] m_PSQTables;
    private enum ScoreType { Pawn, Knight, Bishop, Rook, Queen, King, KingEndgame, KingHunt };

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

    private const ulong k_TpMask = 0xFFFFF;
    private const int k_maxDepth = 4;
    private const int k_qSearchDepth = 3;
    private const int k_endgameDepth = 10;

    private Move m_bestMove;

    public MyBot()
    {
        m_PSQTables = new sbyte[8, 8, 8];
        m_TPTable = new Transposition[0x100000];
        UnpackPSQTables(ref m_PSQTables);
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
        Move[] qSearchMoves = board.GetLegalMoves(true);

        if (!isEndgame) isEndgame = IsEndgame(board);

        //Check for leaf node conditions
        bool qSearch = (depth >= k_maxDepth) && (qSearchMoves.Length != 0);
        if (board.IsDraw()) return 0;
        if (board.IsInCheckmate()) return -(int.MaxValue - depth);

        //Check Transposition Table
        ulong zHash = board.ZobristKey;
        ulong zIndex = zHash & k_TpMask;
        bool addToTpTable = false;
        if(m_TPTable[zIndex].zobristHash == zHash && m_TPTable[zIndex].depth >= depth && m_TPTable[zIndex].move != Move.NullMove)
        {
            if(depth == 0) m_bestMove = m_TPTable[zIndex].move;
            return m_TPTable[zIndex].evaluation;
        }
        else
        {
            addToTpTable = true;
            m_TPTable[zIndex].zobristHash = zHash;
            m_TPTable[zIndex].depth = (byte)depth;
        }

        if (allLegalmoves.Length == 0 || (depth >= maxDepth && !qSearch))
        {
#if DEBUGGING
            d_numPositions++;
#endif
            return Evaluate(board, isEndgame) * color;
        }

        Move[] moves = qSearch ? qSearchMoves : allLegalmoves;
        moves = OrderMoves(moves, board);
        int recordEval = int.MinValue;
        int newMaxDepth = k_maxDepth;
        if (qSearch) newMaxDepth += k_qSearchDepth;
        else if (isEndgame) newMaxDepth += k_endgameDepth;
        foreach (Move m in moves)
        {
            board.MakeMove(m);
            int eval = -Search(board, depth + 1, newMaxDepth, -beta, -alpha, -color, isEndgame);
            board.UndoMove(m);
            if (recordEval < eval)
            {
                recordEval = eval;
                if (depth == 0) m_bestMove = m;
                if(addToTpTable)
                {
                    m_TPTable[zIndex].evaluation  = recordEval;
                    m_TPTable[zIndex].move = m;
                }
            }
            alpha = Math.Max(alpha, eval);
            if (alpha >= beta) break;
        }
        return recordEval;
    }

    private int Evaluate(Board board, bool isEndgame)
    {
        int materialCount = 0;
        int positionalScore = 0;

        for (int i = 0; ++i < 7;)
            materialCount += (board.GetPieceList((PieceType)i, true).Count - board.GetPieceList((PieceType)i, false).Count) * k_pieceValues[i];

        for (int r = 0; r < 8; r++)
        {
            for (int f = 0; f < 8; f++)
            {
                Square currentSquare = new Square(f, r);
                Piece currentPiece = board.GetPiece(currentSquare);
                ScoreType sType = (ScoreType)((int)(currentPiece.PieceType) - 1);
                if (isEndgame)
                {
                    sType++;
                    if (board.IsWhiteToMove ^ currentPiece.IsWhite) sType++;
                }
                if (sType >= 0)
                {
                    positionalScore += GetPieceBonusScore(sType, currentPiece.IsWhite, r, f);
                    if (sType == ScoreType.KingHunt) positionalScore *= -1;
                }
            }
        }
        return materialCount + positionalScore;
    }

    private Move[] OrderMoves(Move[] moves, Board board)
    {
        Move[] orderedMoves = new Move[moves.Length];
        List<Tuple<Move, int>> moveSorter = new();
        foreach (Move m in moves)
        {
            int priority = 0;
            //highly prioritize captures
            if (m.IsCapture) priority += k_mvvValues[(int)m.CapturePieceType] + k_lvaValues[(int)m.MovePieceType];
            Tuple<Move, int> movePriPair = new Tuple<Move, int>(m, priority);
            moveSorter.Add(movePriPair);
        }
        moveSorter = moveSorter.OrderByDescending(x => x.Item2).ToList();
        for (int i = 0; i < moveSorter.Count; i++)
        {
            orderedMoves[i] = moveSorter[i].Item1;
        }
        return orderedMoves;
    }

    private void UnpackPSQTables(ref sbyte[,,] psqTables)
    {
        for (int type = 0; type < 8; type++)
        {
            for (int rank = 0; rank < 8; rank++)
            {
                for (int file = 0; file < 8; file++)
                {
                    int f = file;
                    if (f > 3) f = 7 - f;
                    ulong bytemask = 0xFF;
                    psqTables[type, rank, file] = (sbyte)(kPackedScores[rank, f] & (bytemask << type));
                }
            }
        }
    }

    private int GetPieceBonusScore(ScoreType type, bool isWhite, int rank, int file)
    {
        if (!isWhite) rank = 7 - rank;
        int score = m_PSQTables[(int)type, rank, file];
        if (!isWhite) score *= -1;
        return score;
    }

    private bool IsEndgame(Board board)
    {
        if ((board.GetPieceBitboard(PieceType.Pawn, true) | board.GetPieceBitboard(PieceType.Knight, true) | board.GetPieceBitboard(PieceType.Bishop, true) | board.GetPieceBitboard(PieceType.Rook, true) | board.GetPieceBitboard(PieceType.Queen, true)) == 0) return true;
        else if ((board.GetPieceBitboard(PieceType.Pawn, false) | board.GetPieceBitboard(PieceType.Knight, false) | board.GetPieceBitboard(PieceType.Bishop, false) | board.GetPieceBitboard(PieceType.Rook, false) | board.GetPieceBitboard(PieceType.Queen, false)) == 0) return true;
        return false;
    }
}