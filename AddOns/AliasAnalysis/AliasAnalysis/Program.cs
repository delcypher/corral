﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Boogie;
using btype = Microsoft.Boogie.Type;
using cba.Util;
using Microsoft.Boogie.GraphUtil;

namespace AliasAnalysis
{
    class Driver
    {
        static void Main(string[] args)
        {
            bool doSSA = true;
            var dbg = false;
            string prune = null;

            if (args.Length < 1)
            {
                Console.WriteLine("Usage: AliasAnalysis file.bpl [flags]");
            }

            if (args.Any(s => s == "/break"))
                System.Diagnostics.Debugger.Launch();
            if (args.Any(s => s == "/nossa"))
                doSSA = false;
            if (args.Any(s => s == "/dbg"))
                dbg = true;
            args.Where(s => s.StartsWith("/prune:"))
                .Iter(s => prune = s.Split(':')[1]);

            CommandLineOptions.Install(new CommandLineOptions());
            CommandLineOptions.Clo.PrintInstrumented = true;

            var program = BoogieUtil.ParseProgram(args[0]);
            Program origProgram = null;
            if (prune != null)
                origProgram = (new FixedDuplicator(false)).VisitProgram(program);

            program.Resolve();

            // Make sure that aliasing queries are on identifiers only
            SimplifyAliasingQueries.Simplify(program);

            // Do SSA
            if (doSSA)
            {
                program =
                  SSA.Compute(program, PhiFunctionEncoding.Verifiable, new HashSet<string> { "int" });
                if (dbg) BoogieUtil.PrintProgram(program, "ssa.bpl");
            }

            AliasAnalysis.dbg = dbg;
            AliasConstraintSolver.dbg = dbg;
            var ret =
            AliasAnalysis.DoAliasAnalysis(program);

            foreach (var tup in ret.aliases)
                Console.WriteLine("{0}: {1}", tup.Key, tup.Value);

            if (prune != null)
            {
                origProgram.Resolve();
                PruneAliasingQueries.Prune(origProgram, ret);
                BoogieUtil.PrintProgram(origProgram, prune);
            }
        }

    }

    public class PruneAliasingQueries : FixedVisitor
    {
        AliasAnalysisResults result;
        
        Function AllocationSites;
        Dictionary<string, Constant> asToAS;

        PruneAliasingQueries(AliasAnalysisResults result, Function AllocationSites, Dictionary<string, Constant> asToAS)
        {
            this.result = result;
            this.AllocationSites = AllocationSites;
            this.asToAS = asToAS;
        }

        public static void Prune(Program program, AliasAnalysisResults result)
        {
            Function AllocationSites = null;
            Dictionary<string, Constant> asToAS = new Dictionary<string, Constant>();

            if (result.allocationSites.Any())
            {
                // type AS;
                var asType = new TypeCtorDecl(Token.NoToken, "AS", 0);
                program.AddTopLevelDeclaration(asType);
                // add AS constants
                var sites = new HashSet<string>();
                result.allocationSites.Values.Iter(v => sites.UnionWith(v));
                foreach (var s in sites)
                {
                    var c = new Constant(Token.NoToken,
                            new TypedIdent(Token.NoToken, "AS_" + s, new CtorType(Token.NoToken, asType, new List<btype>())),
                            true);
                    asToAS.Add(s, c);
                    program.AddTopLevelDeclaration(c);
                }
                // Add: function AllocationSites(int) : AS
                AllocationSites = new Function(Token.NoToken, "AllocationSites", new List<Variable>{
                    new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "a", btype.Int), true)},
                    new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "b", new CtorType(Token.NoToken, asType, new List<btype>())), false));
                program.AddTopLevelDeclaration(AllocationSites);
            }

            var prune = new PruneAliasingQueries(result, AllocationSites, asToAS);

            program.TopLevelDeclarations.OfType<Implementation>()
                .Iter(impl => prune.VisitImplementation(impl));
            program.TopLevelDeclarations.OfType<Implementation>()
                .Iter(impl => prune.PruneFalseBranches(impl));
            var main = program.TopLevelDeclarations.OfType<Procedure>()
                .Where(proc => QKeyValue.FindBoolAttribute(proc.Attributes, "entrypoint"))
                .FirstOrDefault();
            if (main != null)
                BoogieUtil.pruneProcs(program, main.Name);

            // remove aliasing queries
            program.TopLevelDeclarations = new List<Declaration>(program.TopLevelDeclarations
                .Where(decl => !(decl is Function && BoogieUtil.checkAttrExists("aliasingQuery", decl.Attributes))));
        }

        public override Expr VisitNAryExpr(NAryExpr node)
        {
            var fcall = node.Fun as FunctionCall;
            if (fcall != null && result.aliases.ContainsKey(fcall.FunctionName))
            {
                if (result.aliases[fcall.FunctionName] == false)
                {
                    return Expr.False;
                }
                else
                {
                    return Expr.Eq(node.Args[0], node.Args[1]);
                }
            }

            if (fcall != null && result.allocationSites.ContainsKey(fcall.FunctionName))
            {
                Expr r = Expr.False;
                foreach (var s in result.allocationSites[fcall.FunctionName])
                    r = Expr.Or(r, Expr.Eq(new NAryExpr(Token.NoToken, new FunctionCall(AllocationSites), new List<Expr>{node.Args[0]}), Expr.Ident(asToAS[s])));
                return r;
            }

            var ret = base.VisitNAryExpr(node);

            var nary = ret as NAryExpr;
            if (nary != null && nary.Fun is BinaryOperator
                && (nary.Fun as BinaryOperator).Op == BinaryOperator.Opcode.And)
            {
                if (nary.Args.Any(a => a == Expr.False))
                    return Expr.False;
            }
            if (nary != null && nary.Fun is UnaryOperator
                && (nary.Fun as UnaryOperator).Op == UnaryOperator.Opcode.Not
                && nary.Args[0] is LiteralExpr
                && (nary.Args[0] as LiteralExpr).IsFalse)
            {
                return Expr.True;
            }

            return ret;
        }

        public void PruneFalseBranches(Implementation impl)
        {
            foreach (var block in impl.Blocks)
            {
                var ncmds = new List<Cmd>();
                foreach (var cmd in block.Cmds)
                {
                    ncmds.Add(cmd);
                    if (BoogieUtil.isAssumeFalse(cmd))
                        break;
                }
                block.Cmds = ncmds;
            }

            var tt = CommandLineOptions.Clo.PruneInfeasibleEdges;
            CommandLineOptions.Clo.PruneInfeasibleEdges = true;
            impl.PruneUnreachableBlocks();
            CommandLineOptions.Clo.PruneInfeasibleEdges = tt;
        }
    }

    public class SimplifyAliasingQueries : FixedVisitor
    {
        Implementation currImpl;
        int counter;
        HashSet<string> aliasingFunctions;
        List<Cmd> extraCmds;

        SimplifyAliasingQueries()
        {
            currImpl = null;
            counter = 0;
            aliasingFunctions = new HashSet<string>();
            extraCmds = new List<Cmd>();
        }

        public static HashSet<string> Simplify(Program program)
        {
            var sa = new SimplifyAliasingQueries();
            sa.VisitProgram(program);
            return sa.aliasingFunctions;
        }

        public override Program VisitProgram(Program node)
        {
            node.TopLevelDeclarations.OfType<Function>()
                .Where(func => BoogieUtil.checkAttrExists("aliasingQuery", func.Attributes))
                .Iter(func => aliasingFunctions.Add(func.Name));

            return base.VisitProgram(node);
        }

        public override Implementation VisitImplementation(Implementation node)
        {
            currImpl = node;
            node = base.VisitImplementation(node);
            currImpl = null;
            return node;
        }

        public override Block VisitBlock(Block node)
        {
            var newcmds = new List<Cmd>();
            foreach (var cmd in node.Cmds)
            {
                extraCmds = new List<Cmd>();
                base.Visit(cmd);
                newcmds.AddRange(extraCmds);
                newcmds.Add(cmd);
            }
            node.Cmds = newcmds;
            return node;
        }

        public override Expr VisitNAryExpr(NAryExpr node)
        {
            if (node.Fun is FunctionCall && aliasingFunctions.Contains((node.Fun as FunctionCall).FunctionName))
            {
                if (currImpl == null) return node;

                for (int i = 0; i < node.Args.Count; i++)
                {
                    if (node.Args[i] is IdentifierExpr) continue;
                    var lv = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, "aaTmp" + (counter++).ToString(),
                        btype.Int));
                    currImpl.LocVars.Add(lv);
                    extraCmds.Add(BoogieAstFactory.MkVarEqExpr(lv, node.Args[i]));
                    node.Args[i] = Expr.Ident(lv);
                }
            }
            return base.VisitNAryExpr(node);
        }
    }

    /*
    public class AliasQuery
    {
        public Variable var1;
        public Implementation impl1;
        public Variable var2;
        public Implementation impl2;

        public AliasQuery(GlobalVariable var1, GlobalVariable var2)
            :this(var1, null, var2, null)
        { }

        public AliasQuery(GlobalVariable var1, Variable var2, Implementation impl2)
            : this(var1, null, var2, impl2)
        { }

        public AliasQuery(Variable var1, Implementation impl1, Variable var2, Implementation impl2)
        {
            this.var1 = var1;
            this.var2 = var2;
            this.impl1 = impl1;
            this.impl2 = impl2;
        }

        public static AliasQuery Parse(string line, Program program)
        {
            var sp = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length != 4)
                return null;
            var nameToImpl = BoogieUtil.nameImplMapping(program);
            var impl1 = nameToImpl.ContainsKey(sp[1]) ? nameToImpl[sp[1]] : null;
            var impl2 = nameToImpl.ContainsKey(sp[3]) ? nameToImpl[sp[3]] : null;
            Variable var1, var2;

            if (impl1 == null)
            {
                var1 = program.TopLevelDeclarations.OfType<GlobalVariable>()
                    .Where(g => g.Name == sp[0]).FirstOrDefault();
            }
            else
            {
                var1 = impl1.LocVars
                    .Where(g => g.Name == sp[0]).FirstOrDefault();
            }
            if (impl2 == null)
            {
                var2 = program.TopLevelDeclarations.OfType<GlobalVariable>()
                    .Where(g => g.Name == sp[2]).FirstOrDefault();
            }
            else
            {
                var2 = impl2.LocVars
                    .Where(g => g.Name == sp[2]).FirstOrDefault();
            }

            if (var1 == null || var2 == null)
                return null;
            return new AliasQuery(var1, impl1, var2, impl2);
        }
    }
    */

    public class AliasAnalysisResults
    {
        public Dictionary<string, bool> aliases;
        public Dictionary<string, HashSet<string>> allocationSites;
        public AliasAnalysisResults()
        {
            aliases = new Dictionary<string, bool>();
            allocationSites = new Dictionary<string, HashSet<string>>();
        }
    }

    public class AliasAnalysis
    {

        Program program;
        HashSet<string> allocatedConstants;
        AliasConstraintSolver solver;
        int counter;
        public static bool dbg = false;
        CSFSAliasAnalysis csfsAnalysis;
        public static HashSet<string> non_null_vars = null; 

        private AliasAnalysis(Program program)
        {
            this.program = program;
            this.allocatedConstants = new HashSet<string>();
            solver = new AliasConstraintSolver();
            counter = 0;
            csfsAnalysis = new CSFSAliasAnalysis(program);
        }

        private void getReturnAllocSites(Dictionary<string, HashSet<string>> PointsTo)
        {
            var impl2aS = new Dictionary<Implementation, Dictionary<int, HashSet<string>>>();
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                var out2aS = new Dictionary<int, HashSet<string>>();
                for (int i = 0; i < impl.Proc.OutParams.Count; i++ )
                {
                    Variable v = impl.Proc.OutParams[i];
                    string s = ConstructVariableName(v, impl.Name);
                    if (PointsTo.ContainsKey(s)) out2aS[i] = PointsTo[s];
                    else out2aS[i] = new HashSet<string>();
                }
                impl2aS[impl] = out2aS;
            }
            csfsAnalysis.getReturnAllocSites(impl2aS, PointsTo);
        }

        /* Looks at the assumes and asserts, figures out their SSA, and ensures that the allocation site corresponding to NULL does not flow into these variables */
        private void Process_Assumes_Asserts()
        {
            non_null_vars = new HashSet<string>();
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (Block b in impl.Blocks)
                {
                    foreach (Cmd c in b.Cmds)
                    {
                        if (c is AssignCmd)
                        {
                            var ac = c as AssignCmd;
                            foreach (AssignLhs lhs in ac.Lhss)
                            {
                                if (lhs is SimpleAssignLhs && lhs.DeepAssignedVariable.Name.StartsWith("cseTmp"))
                                {
                                    non_null_vars.Add(ConstructVariableName(lhs.DeepAssignedVariable, impl.Name));
                                }
                            }
                        }
                    }
                }
            }
        }

        public static AliasAnalysisResults DoAliasAnalysis(Program program)
        {
            var aa = new AliasAnalysis(program);
            if (dbg) Console.WriteLine("Creating Points-to constraints ... ");
            aa.Process();
            aa.Process_Assumes_Asserts();
            if (dbg) Console.WriteLine("Done");
            if (dbg) Console.WriteLine("Solving Points-to constraints ... ");
            aa.solver.Solve();
            aa.getReturnAllocSites(aa.solver.GetPointsToSet());
            if (dbg) Console.WriteLine("Done");

            // Solve the queries
            return
                AliasQuerySolver.Solve(program, new Func<Variable, Variable, Implementation, bool>((v1, v2, impl) => aa.solver.IsAlias(
                    aa.ConstructVariableName(v1, impl.Name),
                    aa.ConstructVariableName(v2, impl.Name))),
                    new Func<Variable, Variable, Implementation, bool>((v1, v2, impl) => aa.solver.IsReachable(
                    aa.ConstructVariableName(v1, impl.Name),
                    aa.ConstructVariableName(v2, impl.Name))),
                    new Func<Variable, Implementation, HashSet<string>>((v1, impl) => aa.solver.AllocationSites(
                    aa.ConstructVariableName(v1, impl.Name)))
                    );
        }

        public static Dictionary<string, bool> DoCSFSAliasAnalysis(Program program)
        {
            var csfsaa = new CSFSAliasAnalysis(program);
            csfsaa.CSFSProcess();
            if (csfsaa.use_map) csfsaa.PreciseMapAnalyze();
            else csfsaa.SoundAnalyze();
            return csfsaa.AnalysisResults;
        }

        class AliasQuerySolver : FixedVisitor
        {
            Func<Variable, Variable, Implementation, bool> IsAlias;
            Func<Variable, Variable, Implementation, bool> IsReachable;
            Func<Variable, Implementation, HashSet<string>> PointsToSet;
            HashSet<string> aliasQueryFuncs;
            HashSet<string> reachableQueryFuncs;
            HashSet<string> allocationSitesQueryFuncs;
            AliasAnalysisResults results;
            Implementation currImpl;

            private AliasQuerySolver(HashSet<string> aliasQueryFuncs, HashSet<string> reachableQueryFuncs, HashSet<string> allocationSitesQueryFuncs, 
                Func<Variable, Variable, Implementation, bool> IsAlias,
                Func<Variable, Variable, Implementation, bool> IsReachable,
                Func<Variable, Implementation, HashSet<string>> PointsToSet)
            {
                this.aliasQueryFuncs = aliasQueryFuncs;
                this.reachableQueryFuncs = reachableQueryFuncs;
                this.allocationSitesQueryFuncs = allocationSitesQueryFuncs;
                this.results = new AliasAnalysisResults();
                this.IsAlias = IsAlias;
                this.IsReachable = IsReachable;
                this.PointsToSet = PointsToSet;
                aliasQueryFuncs.Iter(q => results.aliases.Add(q, false));
                reachableQueryFuncs.Iter(q => results.aliases.Add(q, false));
                allocationSitesQueryFuncs.Iter(q => results.allocationSites.Add(q, new HashSet<string>()));
            }

            public static AliasAnalysisResults Solve(Program program, 
                Func<Variable, Variable, Implementation, bool> IsAlias,
                Func<Variable, Variable, Implementation, bool> IsReachable,
                Func<Variable, Implementation, HashSet<string>> PointsToSet)
            {
                var aq = new HashSet<string>();
                var rq = new HashSet<string>();
                var asq = new HashSet<string>();
                foreach (var func in program.TopLevelDeclarations.OfType<Function>()
                    .Where(func => BoogieUtil.checkAttrExists("aliasingQuery", func.Attributes)))
                {
                    var p = QKeyValue.FindStringAttribute(func.Attributes, "aliasingQuery");
                    if(p != null && p == "transitive") rq.Add(func.Name);
                    else if(p != null && p == "allocationsites") asq.Add(func.Name);
                    else aq.Add(func.Name);
                }

                var qSolver = new AliasQuerySolver(aq, rq, asq, IsAlias, IsReachable, PointsToSet);
                program.TopLevelDeclarations.OfType<Implementation>()
                    .Iter(impl =>
                    {
                        qSolver.currImpl = impl;
                        qSolver.VisitImplementation(impl);
                    });

                return qSolver.results;
            }

            public override Expr VisitNAryExpr(NAryExpr node)
            {
                if (node.Fun is FunctionCall)
                {
                    var func = (node.Fun as FunctionCall).FunctionName;
                    if (aliasQueryFuncs.Contains(func) || reachableQueryFuncs.Contains(func))
                    {
                        Debug.Assert(node.Args.Count == 2);
                        var arg1 = node.Args[0] as IdentifierExpr;
                        var arg2 = node.Args[1] as IdentifierExpr;
                        Debug.Assert(arg1 != null && arg2 != null);
                        if (!results.aliases[func])
                        {
                            results.aliases[func] = aliasQueryFuncs.Contains(func) ?
                                IsAlias(arg1.Decl, arg2.Decl, currImpl) :
                                IsReachable(arg1.Decl, arg2.Decl, currImpl);

                        }
                    }
                    if (allocationSitesQueryFuncs.Contains(func))
                    {
                        Debug.Assert(node.Args.Count == 1);
                        var arg1 = node.Args[0] as IdentifierExpr;
                        Debug.Assert(arg1 != null);
                        results.allocationSites[func].UnionWith(
                            PointsToSet(arg1.Decl, currImpl));
                    }

                }
                return base.VisitNAryExpr(node);
            }
        }

        private void Process()
        {
            // Find the allocators
            var cmd2AllocationConstraint = new Dictionary<Tuple<Implementation, Block, Cmd>, string>();
            var allocators = new HashSet<string>();
            program.TopLevelDeclarations.OfType<Procedure>()
                .Where(proc => BoogieUtil.checkAttrExists("allocator", proc.Attributes))
                .Iter(proc => allocators.Add(proc.Name));

            var funkyAllocators = new HashSet<string>();
            program.TopLevelDeclarations.OfType<Procedure>()
                .Where(proc => QKeyValue.FindStringAttribute(proc.Attributes, "allocator") == "full")
                .Iter(proc => funkyAllocators.Add(proc.Name));

            var nameToImpl = BoogieUtil.nameImplMapping(program);

            // Add allocated global constants
            program.TopLevelDeclarations.OfType<Constant>()
                .Where(c => QKeyValue.FindBoolAttribute(c.Attributes, "allocated"))
                .Iter(c =>
                {
                    allocatedConstants.Add(c.Name);
                    solver.Add(new AllocationConstraint(ConstructVariableName(c, null), false));
                });

            foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (var block in impl.Blocks)
                {
                    foreach (var cmd in block.Cmds)
                    {
                        var assign = cmd as AssignCmd;
                        if (assign != null)
                            for (int i = 0; i < assign.Lhss.Count; i++)
                                ProcessAssignment(assign.Lhss[i], impl.Name, assign.Rhss[i], impl.Name);
                        var ccmd = cmd as CallCmd;
                        if (ccmd != null)
                        {
                            if (allocators.Contains(ccmd.callee) && ccmd.Outs.Count == 1)
                            {
                                cmd2AllocationConstraint.Add(new Tuple<Implementation, Block, Cmd>(impl, block, ccmd), AllocationConstraint.getSite());
                                solver.Add(new AllocationConstraint(ConstructVariableName(ccmd.Outs[0].Decl, impl.Name), funkyAllocators.Contains(ccmd.callee)));
                            }
                            else if (nameToImpl.ContainsKey(ccmd.callee))
                            {
                                var callee = nameToImpl[ccmd.callee];
                                // formals := actuals
                                for (int i = 0; i < callee.InParams.Count; i++)
                                    ProcessAssignment(new SimpleAssignLhs(Token.NoToken, Expr.Ident(callee.InParams[i])),
                                        callee.Name, ccmd.Ins[i], impl.Name);
                                // outs := return
                                for (int i = 0; i < callee.OutParams.Count; i++)
                                    ProcessAssignment(new SimpleAssignLhs(Token.NoToken, ccmd.Outs[i]), impl.Name,
                                        Expr.Ident(callee.OutParams[i]), callee.Name);
                            }
                        }
                    }
                }
            }
            csfsAnalysis.getcmd2AllocMapping(cmd2AllocationConstraint);
        }

        private void ProcessAssignment(AssignLhs target, string targetProcName, Expr source, string sourceProcName)
        {
            var rs = ReadSet.Get(source);
            if (target is SimpleAssignLhs)
            {
                ProcessAssignment(ConstructVariableName(target.DeepAssignedVariable, targetProcName), rs, sourceProcName);
                return;
            }

            var massign = target as MapAssignLhs;
            var loadMap = massign.DeepAssignedVariable.Name;
            var loadTargetTmp = "tmpVar" + (counter++).ToString();
            var storeTargetTmp = "tmpVar" + (counter++).ToString();
            var indexRead = ReadSet.Get(massign.Indexes);

            ProcessAssignment(loadTargetTmp, indexRead, sourceProcName);
            ProcessAssignment(storeTargetTmp, rs, sourceProcName);
            solver.Add(new StoreConstraint(storeTargetTmp, loadTargetTmp, loadMap));
        }

        private void ProcessAssignment(string target, HashSet<Tuple<Variable, List<string>>> sourceReadSet, string sourceProcName)
        {
            var x = target;
            foreach (var tup in sourceReadSet)
            {
                var y = ConstructVariableName(tup.Item1, sourceProcName);
                if (tup.Item2.Count == 0)
                    solver.Add(new AssignConstraint(y, x));
                else
                {
                    var currTarget = x;
                    for (int i = 0; i < tup.Item2.Count; i++)
                    {
                        var currSource = (i == tup.Item2.Count - 1) ? y : ("tmpVar" + (counter++).ToString());
                        solver.Add(new LoadConstraint(currSource, currTarget, tup.Item2[i]));
                        currTarget = currSource;
                    }
                }
            }
        }

        private string ConstructVariableName(Variable v, string procName)
        {
            if (v is GlobalVariable || allocatedConstants.Contains(v.Name))
                return v.Name;
            return v.Name + "$" + procName;
        }


    }

    /*
     * CSFS Alias Analysis :- Context Sensitive Flow Sensitive Alias Analysis
     * 1. Build an intraprocedural flow sensitive transfer function for each procedure which takes in the allocation sites of the input parameters, and returns the output parameters.
     * 2. The transfer function has levels, upto a context bound, which can be increased to improve precision.
     * 3. The transfer function is a recursive function, which calls the transfer function of immediately lesser depth for the callees.
     * 4. The transfer function of depth 0 just falls on the old alias analysis.
     * 5. Once the transfer function is built, we analyze every procedure with the biggest overapproximation of the input parameters obtained from the old alias analysis.
     * 6. We introduce asserts at a certain depth, since it gives context sensitivity, and the transfer function gives flow sensitivity.
     * 7. For scalability reasons, we have set the context bound to 3, and we add asserts at a depth of 1.
    */

    public class CSFSAliasAnalysis
    {
        Program program;
        int Context_Bound;
        int Assert_Depth;
        int Caller_Bound;
        static Dictionary<string, HashSet<string>> PointsTo = null;             // Keep a copy of the Points To Set
        Dictionary<string, Implementation> name2Impl;                           // Mapping from implementation name to implementation object
        static Dictionary<Tuple<Implementation, Block, Cmd>, string> cmd2AllocationConstraint = null;       // Use the same allocation sites as the old alias analysis, hence using a mapping between commands and allocation sites
        static Dictionary<Implementation, Dictionary<int, HashSet<string>>> ret_allocSites = null;          // Dictionary from implementation to the allocation sites of the output parameters
        Dictionary<Tuple<Implementation, int>, Func<Dictionary<int, HashSet<string>>, Dictionary<int, HashSet<string>>>> transfer_function;     // Dictionary of transfer functions for each level for each implementation
        public Dictionary<string, bool> AnalysisResults;                    // Store the results of analysis here
        HashSet<string> singleAllocSites;                                   // The allocation sites which correspond to at most 1 concrete heap location
        bool dbg = false;                                                   // Debugging flag
        StronglyConnectedComponents<Implementation> stronglyConnectedComponents;
        Dictionary<string, HashSet<string>> MapPointsTo;
        public bool use_map = false;
        Dictionary<Implementation, HashSet<Implementation>> Pred;
        HashSet<Implementation> implementationsUptoBound;
        private static string null_alloc_site;

        public CSFSAliasAnalysis(Program prog)
        {
            program = prog;
            Context_Bound = 3;
            Caller_Bound = 0;
            transfer_function = new Dictionary<Tuple<Implementation, int>, Func<Dictionary<int, HashSet<string>>, Dictionary<int, HashSet<string>>>>();
            name2Impl = BoogieUtil.nameImplMapping(prog);
            AnalysisResults = new Dictionary<string, bool>();
            Debug.Assert(Caller_Bound <= Context_Bound);
            singleAllocSites = new HashSet<string>();
            MapPointsTo = new Dictionary<string, HashSet<string>>();
            Pred = new Dictionary<Implementation, HashSet<Implementation>>();
            implementationsUptoBound = new HashSet<Implementation>();
        }

        /*
         * Looks at the old alias analysis to figure out the biggest overapproximation of the allocation sites of the output parameters of each implementation
         * Used as a widen operator, to get sound overapproximate analysis
        */
        public void getReturnAllocSites(Dictionary<Implementation, Dictionary<int, HashSet<string>>> dict, Dictionary<string, HashSet<string>> PTS)
        {
            ret_allocSites = dict;
            PointsTo = PTS;
            if (dbg) PointsTo.Keys.Iter(s =>
                {
                    Console.WriteLine(s);
                    foreach (string aS in PointsTo[s]) Console.Write(aS + " ");
                    Console.WriteLine();
                });
            null_alloc_site = PointsTo.ContainsKey("NULL") ? PointsTo["NULL"].First() : null;
        }

        /*
         * Mapping from (implementation, block, command) -> allocation site
        */
        public void getcmd2AllocMapping(Dictionary<Tuple<Implementation, Block, Cmd>, string> dict)
        {
            cmd2AllocationConstraint = dict;
        }

        /*
         * Creates the transfer function for each implementation of each depth.
         * The main idea behind the transfer function is an Andersen based analysis, with some strong updates.
        */
        private void CreateTransferFunction(int current_depth)
        {
            // Finding allocator procedures, so that we can add allocation sites for such procedure calls
            var allocators = new HashSet<string>();
            program.TopLevelDeclarations.OfType<Procedure>()
                .Where(proc => BoogieUtil.checkAttrExists("allocator", proc.Attributes))
                .Iter(proc => allocators.Add(proc.Name));

            program.TopLevelDeclarations.OfType<Procedure>()
                .Where(proc => QKeyValue.FindStringAttribute(proc.Attributes, "allocator") == "full")
                .Iter(proc => allocators.Add(proc.Name));

            // BlockPointsTo contains the points to set for each block in an implementation
            Dictionary<Block, Dictionary<string, HashSet<string>>> BlockPointsTo;
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                // Creating transfer function of a certain depth
                transfer_function[new Tuple<Implementation, int>(impl, current_depth)] =
                    (in_params) =>
                    {
                        if (dbg) Console.WriteLine(impl.Name + " => ");

                        // Topological sorting on the blocks
                        IEnumerable<Block> sortedBlocks;
                        impl.ComputePredecessorsForBlocks();
                        Microsoft.Boogie.GraphUtil.Graph<Block> dag = Microsoft.Boogie.Program.GraphFromImpl(impl);
                        sortedBlocks = dag.TopologicalSort();

                        // Adding the allocation sites of the input parameters of the implementation in the BlockPointsTo of entry block
                        BlockPointsTo = new Dictionary<Block, Dictionary<string, HashSet<string>>>();
                        BlockPointsTo.Add(sortedBlocks.First(), new Dictionary<string, HashSet<string>>());
                        if (dbg) Console.WriteLine("InParams for {0} :-", impl.Name);

                        for (int i = 0; i < impl.Proc.InParams.Count; i++)
                        {
                            BlockPointsTo[sortedBlocks.First()][impl.Proc.InParams[i].Name] = new HashSet<string>();
                            if (dbg) Console.WriteLine(impl.Proc.InParams[i].Name);
                            foreach (var aS in in_params[i])
                            {
                                if (dbg) Console.Write(aS + " ");
                                BlockPointsTo[sortedBlocks.First()][impl.Proc.InParams[i].Name].Add(aS);
                            }
                            if (dbg) Console.WriteLine();
                        }

                        // List of exit blocks of implementation
                        List<Block> return_blocks = new List<Block>();

                        // Do the analysis for each block in the implementation in a topological order
                        foreach (Block b in sortedBlocks)
                        {
                            if (dbg) Console.WriteLine(b.Label + " -> ");
                            if (!BlockPointsTo.ContainsKey(b)) BlockPointsTo[b] = new Dictionary<string, HashSet<string>>();

                            // Calculate the allocation sites of each variable in each predecessor, and union them up
                            foreach (Block blk in b.Predecessors)
                            {
                                if (dbg) Console.WriteLine(blk.Label + " -> ");
                                foreach (string st in BlockPointsTo[blk].Keys)
                                {
                                    if (!BlockPointsTo[b].ContainsKey(st)) BlockPointsTo[b][st] = new HashSet<string>();
                                    if (dbg) Console.WriteLine("Block - {0}, String - {1}", blk.Label, st);
                                    foreach (string aS in BlockPointsTo[blk][st])
                                    {
                                        if (!BlockPointsTo[b][st].Contains(aS)) BlockPointsTo[b][st].Add(aS);
                                        if (dbg) Console.Write(aS + " ");
                                    }
                                    if (dbg) Console.WriteLine();
                                }
                            }

                            // If implementation can return from here, add the block to return blocks
                            if (b.TransferCmd is ReturnCmd) return_blocks.Add(b);

                            // Analyze for each command in the block
                            foreach (Cmd c in b.Cmds)
                            {
                                if (dbg) Console.WriteLine("{0}{1} {2} {3} {4}", c.ToString(), c.GetType(), impl.Name, b.Label, current_depth);

                                if (c is AssignCmd)
                                {
                                    var ac = c as AssignCmd;
                                    if (dbg) foreach (string st in BlockPointsTo[b].Keys) Console.WriteLine("PTS contains {0}", st);
                                    
                                    // Analyze each assign statement in assign cmd
                                    for (int i = 0; i < ac.Lhss.Count; i++)
                                    {
                                        // LHS is a variable, which means we can perform strong updates
                                        if (ac.Lhss[i] is SimpleAssignLhs)
                                        {
                                            var lhs = ac.Lhss[i] as SimpleAssignLhs;
                                            if (dbg) Console.WriteLine("Simple -> {0} ", lhs.DeepAssignedVariable.Name);
                                            
                                            if (!BlockPointsTo[b].ContainsKey(ac.Lhss[i].DeepAssignedVariable.Name))
                                            {
                                                BlockPointsTo[b].Add(ac.Lhss[i].DeepAssignedVariable.Name, new HashSet<string>());
                                                if (dbg) Console.WriteLine("Adding {0} to PTS", ac.Lhss[i].DeepAssignedVariable.Name);
                                            }
                                            
                                            // Remove allocation sites from LHS
                                            BlockPointsTo[b][ac.Lhss[i].DeepAssignedVariable.Name].Clear();
                                            
                                            // Put allocation sites of RHS into LHS
                                            foreach (string aS in getExprPointsTo(ac.Rhss[i], BlockPointsTo[b]))
                                            {
                                                BlockPointsTo[b][ac.Lhss[i].DeepAssignedVariable.Name].Add(aS);
                                                if (dbg) Console.Write(aS + " ");
                                            }
                                            if (dbg) Console.WriteLine();
                                        }
                                        else
                                        {
                                            // LHS is a map, since we perform no improvement in precision of maps, we do nothing here
                                            var massign = ac.Lhss[i] as MapAssignLhs;
                                            Debug.Assert(massign.Indexes.Count == 1);
                                            /*
                                             * Should something be done here?
                                             */
                                            if (use_map)
                                            {
                                                HashSet<string> load_aS = getExprPointsTo(massign.Indexes.First(), BlockPointsTo[b]);
                                                if (load_aS.Count == 1 && singleAllocSites.Contains(load_aS.First()))
                                                {
                                                    string var_name = GetODotf(load_aS.First(), massign.DeepAssignedVariable.Name);
                                                    Debug.Assert(MapPointsTo.ContainsKey(var_name));
                                                    MapPointsTo[var_name].Clear();
                                                    foreach (string aS in getExprPointsTo(ac.Rhss[i], BlockPointsTo[b])) MapPointsTo[var_name].Add(aS);
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (c is CallCmd)
                                {
                                    var cc = c as CallCmd;
                                    var ins = new Dictionary<int, HashSet<string>>();

                                    // Get allocation sites for input parameters of callee
                                    for (int i = 0 ; i < cc.Ins.Count ; i++) ins[i] = getExprPointsTo(cc.Ins[i], BlockPointsTo[b]);
                                    var outs = new Dictionary<int, HashSet<string>>();

                                    // Check if callee is an allocator, if not, call transfer function of immediately lower depth
                                    if (allocators.Contains(cc.callee) && cc.Outs.Count == 1)
                                    {
                                        outs[0] = new HashSet<string>();
                                        outs[0].Add(cmd2AllocationConstraint[new Tuple<Implementation, Block, Cmd>(impl, b, c)]);
                                    }
                                    else if (name2Impl.ContainsKey(cc.callee)) outs = transfer_function[new Tuple<Implementation, int>(name2Impl[cc.callee], current_depth - 1)](ins);
                                    // Put allocation sites in output parameters of callee
                                    for (int i = 0 ; i < cc.Outs.Count ; i++)
                                    {
                                        IdentifierExpr id = cc.Outs[i];
                                        if (!BlockPointsTo[b].ContainsKey(id.Decl.Name))
                                        {
                                            BlockPointsTo[b].Add(id.Decl.Name, new HashSet<string>());
                                            if (dbg) Console.WriteLine("Adding {0} to PTS", id.Decl.Name);
                                        }
                                        BlockPointsTo[b][id.Decl.Name].Clear();
                                        foreach (string aS in outs[i])
                                        {
                                            BlockPointsTo[b][id.Decl.Name].Add(aS);
                                            if (dbg) Console.Write(aS + " ");
                                        }
                                        if (dbg) Console.WriteLine();
                                    }
                                }
                                else if (c is AssumeCmd)
                                {
                                    // assume (var != NULL)
                                    // Remove allocation site corresponding to NULL from BlockPointsTo
                                    var asc = c as AssumeCmd;
                                    if (CleanAssert.validAssume(asc))
                                    {
                                        IdentifierExpr id = CleanAssert.getVarFromAssume(asc);
                                        if (id.Decl is GlobalVariable)
                                        {
                                            if (!BlockPointsTo[b].ContainsKey(id.Decl.Name))
                                            {
                                                BlockPointsTo[b].Add(id.Decl.Name, new HashSet<string>());
                                                foreach (var aS in PointsTo[id.Decl.Name]) BlockPointsTo[b][id.Decl.Name].Add(aS);
                                            }
                                        }
                                        if (!BlockPointsTo[b].ContainsKey(id.Decl.Name)) BlockPointsTo[b].Add(id.Decl.Name, new HashSet<string>());
                                        if (BlockPointsTo[b][id.Decl.Name].Contains(null_alloc_site)) BlockPointsTo[b][id.Decl.Name].Remove(null_alloc_site);
                                    }
                                }
                                else if (c is AssertCmd)
                                {
                                    // assert (var != NULL)
                                    // Analyze assertion, and delete it if BlockPointsTo says it never fails
                                    var ac = c as AssertCmd;
                                    
                                    if (CleanAssert.validAssert(ac))
                                    {
                                        IdentifierExpr id = CleanAssert.getVarFromAssert(ac);
                                        string query = CleanAssert.getQueryFromAssert(ac);
                                        if (use_map)
                                        {
                                            if (!AnalysisResults.ContainsKey(query))
                                            {
                                                if (implementationsUptoBound.Contains(impl)) AnalysisResults.Add(query, true);
                                                else AnalysisResults.Add(query, false);
                                            }
                                        }
                                        else if (current_depth == Assert_Depth && !AnalysisResults.ContainsKey(query))
                                        {
                                            AnalysisResults.Add(query, true);
                                            if (dbg) Console.WriteLine(query);
                                        }
                                        if (id.Decl is GlobalVariable)
                                        {
                                            if (!BlockPointsTo[b].ContainsKey(id.Decl.Name))
                                            {
                                                BlockPointsTo[b].Add(id.Decl.Name, new HashSet<string>());
                                                foreach (var aS in PointsTo[id.Decl.Name]) BlockPointsTo[b][id.Decl.Name].Add(aS);
                                            }
                                        }
                                        if (!BlockPointsTo[b].ContainsKey(id.Decl.Name)) BlockPointsTo[b].Add(id.Decl.Name, new HashSet<string>());
                                        if (BlockPointsTo[b][id.Decl.Name].Contains(null_alloc_site) && (current_depth == Assert_Depth || use_map)) AnalysisResults[query] = false;
                                    }
                                }
                                
                            }
                        }

                        // Build output parameters of implementation
                        var out_params = new Dictionary<int, HashSet<string>>();
                        if (dbg) Console.WriteLine("OutParams for {0} :-", impl.Name);
                        for (int i = 0; i < impl.Proc.OutParams.Count(); i++)
                        {
                            out_params[i] = new HashSet<string>();
                            if (dbg) Console.WriteLine(impl.Proc.OutParams[i].Name);
                            if (return_blocks.Count != 0)
                            {
                                // Figure out allocation sites of each output parameter in each return block
                                foreach (Block blk in return_blocks)
                                {
                                    if (BlockPointsTo[blk].ContainsKey(impl.Proc.OutParams[i].Name))
                                    {
                                        foreach (string aS in BlockPointsTo[blk][impl.Proc.OutParams[i].Name])
                                        {
                                            out_params[i].Add(aS);
                                            if (dbg) Console.Write(aS + " ");
                                        }
                                        if (dbg) Console.WriteLine();
                                    }
                                }
                            }
                            else out_params = ret_allocSites[impl];
                        }
                        return out_params;
                    };
            }
        }

        private void UseAssumeCmds()
        {
            int counter = 0;
            string null_alloc_site = PointsTo["NULL"].First();
            Dictionary<Block,Dictionary<string,HashSet<string>>> BlockPointsTo;
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                if (dbg) Console.WriteLine(impl.Name + " => ");
                BlockPointsTo = new Dictionary<Block,Dictionary<string,HashSet<string>>>();
                IEnumerable<Block> sortedBlocks;
                impl.ComputePredecessorsForBlocks();
                Microsoft.Boogie.GraphUtil.Graph<Block> dag = Microsoft.Boogie.Program.GraphFromImpl(impl);
                sortedBlocks = dag.TopologicalSort();
                foreach (Block b in sortedBlocks)
                {
                    var removal_list = new HashSet<AssertCmd>();
                    if (dbg) Console.WriteLine(b.Label + " -> ");
                    BlockPointsTo[b] = new Dictionary<string, HashSet<string>>();
                    foreach (Block blk in b.Predecessors)
                    {
                        foreach (string st in BlockPointsTo[blk].Keys)
                        {
                            if (!BlockPointsTo[b].ContainsKey(st)) BlockPointsTo[b][st] = new HashSet<string>();
                            if (dbg) Console.WriteLine("Block - {0}, String - {1}", blk.Label, st);
                            foreach (string aS in BlockPointsTo[blk][st]) if (!BlockPointsTo[b][st].Contains(aS)) BlockPointsTo[b][st].Add(aS);
                        }
                    }
                    foreach (Cmd c in b.Cmds)
                    {
                        if (dbg) Console.Write(c.ToString());
                        if (c is AssumeCmd)
                        {
                            var asc = c as AssumeCmd;
                            if (CleanAssert.validAssume(asc))
                            {
                                IdentifierExpr id = CleanAssert.getVarFromAssume(asc);
                                string var_name = ConstructVariableName(id.Decl, impl.Name);
                                if (dbg) Console.WriteLine(var_name);
                                if (!BlockPointsTo[b].ContainsKey(var_name)) BlockPointsTo[b].Add(var_name, new HashSet<string>());
                                if (BlockPointsTo[b][var_name].Contains(null_alloc_site)) BlockPointsTo[b][var_name].Remove(null_alloc_site);
                            }
                        }
                        if (c is AssertCmd)
                        {
                            var ac = c as AssertCmd;
                            if (CleanAssert.validAssert(ac))
                            {
                                IdentifierExpr id = CleanAssert.getVarFromAssert(ac);
                                string var_name = ConstructVariableName(id.Decl, impl.Name);
                                if (dbg) Console.WriteLine(var_name);
                                if (id.Decl is GlobalVariable)
                                {
                                    BlockPointsTo[b].Add(var_name, new HashSet<string>());
                                    foreach (var aS in PointsTo[var_name]) BlockPointsTo[b][var_name].Add(aS);
                                }
                                if (!PointsTo.ContainsKey(var_name)) continue;
                                if (!BlockPointsTo[b].ContainsKey(var_name)) BlockPointsTo[b].Add(var_name, new HashSet<string>());
                                //foreach (string s in PointsTo[var_name]) BlockPointsTo[b][var_name].Add(s);
                                if (!BlockPointsTo[b][var_name].Contains(null_alloc_site))
                                {
                                    removal_list.Add(ac);
                                    counter++;
                                    if (dbg) Console.Write("Removing assert : {0}", ac);
                                }
                            }
                        }
                        if (c is CallCmd)
                        {
                            var cc = c as CallCmd;
                            foreach (IdentifierExpr id in cc.Outs)
                            {
                                string var_name = ConstructVariableName(id.Decl, impl.Name);
                                if (!BlockPointsTo[b].ContainsKey(var_name)) BlockPointsTo[b].Add(var_name, new HashSet<string>());
                                BlockPointsTo[b][var_name].Clear();
                                if (PointsTo.ContainsKey(var_name)) foreach (string s in PointsTo[var_name]) BlockPointsTo[b][var_name].Add(s);
                            }
                        }
                        if (c is AssignCmd)
                        {
                            var asc = c as AssignCmd;
                            for (int i = 0; i < asc.Lhss.Count; i++)
                            {
                                if (asc.Lhss[i] is SimpleAssignLhs)
                                {
                                    var lhs = asc.Lhss[i] as SimpleAssignLhs;
                                    string var_name = ConstructVariableName(lhs.DeepAssignedVariable, impl.Name);
                                    if (!BlockPointsTo[b].ContainsKey(var_name)) BlockPointsTo[b].Add(var_name, new HashSet<string>());
                                    BlockPointsTo[b][var_name].Clear();
                                    BlockPointsTo[b][var_name] = getExprPointsTo(asc.Rhss[i], impl.Name);
                                    if (dbg) Console.WriteLine("Simple -> {0} ", lhs.DeepAssignedVariable.Name);
                                }
                                else
                                {
                                    var massign = asc.Lhss[i] as MapAssignLhs;
                                    Debug.Assert(massign.Indexes.Count == 1);
                                    string map = massign.DeepAssignedVariable.Name;
                                    HashSet<string> rhs = getExprPointsTo(asc.Rhss[i], impl.Name);
                                    foreach (string aS in getExprPointsTo(massign.Indexes[0], impl.Name))
                                    {
                                        string var_name = GetODotf(aS, map);
                                        if (!BlockPointsTo[b].ContainsKey(var_name)) BlockPointsTo[b].Add(var_name, new HashSet<string>());
                                        foreach (string site in rhs) BlockPointsTo[b][var_name].Add(site);
                                    }
                                }
                            }
                        }
                    }
                    foreach (AssertCmd ac in removal_list) b.Cmds.Remove(ac);
                }
            }
            Console.WriteLine("{0} asserts removed by Using Assume Cmds", counter);
        }

        private HashSet<string> getExprPointsTo(Expr expr, string procName)
        {
            var set = new HashSet<string>();
            if (expr is IdentifierExpr)
            {
                IdentifierExpr id = expr as IdentifierExpr;
                string var_name = ConstructVariableName(id.Decl, procName);
                if (!BoogieUtil.checkAttrExists("pointer", id.Decl.Attributes) || !PointsTo.ContainsKey(var_name)) return set;
                foreach (var aS in PointsTo[var_name]) set.Add(aS);
                return set;
            }
            else if (expr is NAryExpr)
            {
                NAryExpr nexpr = expr as NAryExpr;
                if (dbg) Console.WriteLine("NAryExpr :- {0}, {1}", nexpr.ToString(), nexpr.Fun.FunctionName.ToString());
                string map = nexpr.Fun.FunctionName;
                foreach (string aS in getExprPointsTo(nexpr.Args[0], procName))
                {
                    string var_name = GetODotf(aS, map);
                    if (!PointsTo.ContainsKey(var_name)) continue;
                    foreach (string site in PointsTo[var_name]) set.Add(site);
                }
                if (dbg) Console.WriteLine();
                return set;
            }
            else
            {
                if (dbg) Console.WriteLine("Invalid Expression :- {0}", expr.ToString());
                return set;
            }
        }

        /*
         * Gives back the allocation sites an expression can occupy
         * If expression is a scalar, it returns an empty set
         * Recursive function for recursive maps, M1[M2[M3...[x]...]]
        */
        private HashSet<string> getExprPointsTo(Expr expr, Dictionary<string, HashSet<string>> PTS)
        {
            var set = new HashSet<string>();
            var map_set = new HashSet<string>();
            var ret = new HashSet<string>();
            var rs = ReadSet.Get(expr);
            int count = 0;
            if (dbg) Console.WriteLine(expr.ToString());
            foreach (var tup in rs)
            {
                if (dbg)
                {
                    Console.WriteLine("{0} -> {1}", count, tup.Item1);
                    foreach (string mp in tup.Item2) Console.Write(mp + " ");
                    Console.WriteLine();
                    count++;
                }
                string var_name = tup.Item1.Name;
                if (dbg) Console.WriteLine(var_name);
                if (tup.Item1 is GlobalVariable || tup.Item1 is Constant)
                {
                    if (PointsTo.ContainsKey(var_name)) foreach (string aS in PointsTo[var_name]) set.Add(aS);
                    return set;
                }
                if (PTS.ContainsKey(var_name))
                {
                    foreach (var aS in PTS[var_name])
                    {
                        if (dbg) Console.Write(aS + " ");
                        set.Add(aS);
                    }
                    if (dbg) Console.WriteLine();
                }
                if (tup.Item2.Count == 0)
                {
                    foreach (var aS in set) ret.Add(aS);
                }
                else
                {
                    for (int i = tup.Item2.Count - 1 ; i >= 0 ; i--)
                    {
                        string map = tup.Item2.ElementAt(i);
                        foreach (var aS in set)
                        {
                            string of = GetODotf(aS, map);
                            if (PointsTo.ContainsKey(of)) foreach (var alloc_site in PointsTo[of]) map_set.Add(alloc_site);
                        }
                        set.Clear();
                        foreach (var alloc_site in map_set) set.Add(alloc_site);
                        map_set.Clear();
                    }
                    foreach (var aS in set) ret.Add(aS);
                }
            }
            if (ret.Count == 0) if (dbg) Console.WriteLine("IGNORE!");
            return ret;
        }

        private string ConstructVariableName(Variable v, string procName)
        {
            if (v is GlobalVariable)
                return v.Name;
            return v.Name + "$" + procName;
        }

        private string GetODotf(string o, string f)
        {
            return "allocConstruct$" + o + "$" + f;
        }

        private bool isODotf(string of)
        {
            return of.StartsWith("allocConstruct$");
        }

        /*
         * Creating transfer function for all implementations
        */
        public void CSFSProcess()
        {
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                transfer_function[new Tuple<Implementation, int>(impl, 0)] =
                    (in_params) =>
                    {
                        return ret_allocSites[impl];
                    };
            }
            for (int i = 1 ; i <= Context_Bound ; i++) CreateTransferFunction(i);
        }

        public void CallerSoundAnalyze()
        {
            var Succ = new Dictionary<Implementation, HashSet<Implementation>>();
            var Pred = new Dictionary<Implementation, HashSet<Implementation>>();
            name2Impl.Values.Iter(impl => Succ.Add(impl, new HashSet<Implementation>()));
            name2Impl.Values.Iter(impl => Pred.Add(impl, new HashSet<Implementation>()));

            foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (var blk in impl.Blocks)
                {
                    foreach (var cmd in blk.Cmds.OfType<CallCmd>())
                    {
                        if (!name2Impl.ContainsKey(cmd.callee)) continue;
                        Succ[impl].Add(name2Impl[cmd.callee]);
                        Pred[name2Impl[cmd.callee]].Add(impl);
                    }
                }
            }

            var callers = new Dictionary<Implementation, HashSet<Tuple<Implementation, int>>>();
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                callers.Add(impl, new HashSet<Tuple<Implementation, int>>());
                Queue<Tuple<Implementation, int>> pred_queue = new Queue<Tuple<Implementation, int>>();
                pred_queue.Enqueue(new Tuple<Implementation, int>(impl, 0));
                while (pred_queue.Count != 0)
                {
                    var tup = pred_queue.Dequeue();
                    Debug.Assert(Pred.ContainsKey(tup.Item1));
                    if (Pred[tup.Item1].Count == 0 || tup.Item2 == Caller_Bound)
                    {
                        if (!callers[impl].Contains(tup)) callers[impl].Add(tup);
                    }
                    else foreach (Implementation pred_impl in Pred[tup.Item1]) pred_queue.Enqueue(new Tuple<Implementation, int>(pred_impl, tup.Item2 + 1));
                }
            }
            Dictionary<Implementation, Dictionary<int, HashSet<string>>> in_params = new Dictionary<Implementation, Dictionary<int, HashSet<string>>>();
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                in_params.Add(impl, new Dictionary<int,HashSet<string>>());
                for (int i = 0 ; i < impl.Proc.InParams.Count ; i++)
                {
                    HashSet<string> set = new HashSet<string>();
                    string var_name = ConstructVariableName(impl.Proc.InParams[i], impl.Name);
                    if (PointsTo.ContainsKey(var_name))
                    {
                        foreach (string aS in PointsTo[var_name]) set.Add(aS);
                    }
                    in_params[impl].Add(i, set);
                }
            }

            Dictionary<int, HashSet<string>> out_params = new Dictionary<int, HashSet<string>>();
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (Tuple<Implementation, int> tup in callers[impl])
                {
                    Assert_Depth = Context_Bound - tup.Item2;
                    out_params = transfer_function[new Tuple<Implementation, int>(tup.Item1, Context_Bound)](in_params[tup.Item1]);
                }
            }
        }

        /*
         * Do analysis once the transfer functions are created
        */
        public void SoundAnalyze()
        {
            // Go to each implementation
            foreach (Implementation impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                Assert_Depth = Context_Bound - Caller_Bound;
                Dictionary<int, HashSet<string>> in_params = new Dictionary<int, HashSet<string>>();
                Dictionary<int, HashSet<string>> out_params = new Dictionary<int, HashSet<string>>();

                // Figure out the biggest overapproximations of the input parameters of the implementation
                for (int i = 0 ; i < impl.Proc.InParams.Count ; i++)
                {
                    HashSet<string> set = new HashSet<string>();
                    string var_name = ConstructVariableName(impl.Proc.InParams[i], impl.Name);
                    if (PointsTo.ContainsKey(var_name))
                    {
                        foreach (string aS in PointsTo[var_name]) set.Add(aS);
                    }
                    in_params.Add(i, set);
                }
                out_params = transfer_function[new Tuple<Implementation, int>(impl, Context_Bound)](in_params);
            }
        }

        public void UnsoundAnalyze()
        {
            // Construct the call graph and compute strongly connected components
            var Succ = new Dictionary<Implementation, HashSet<Implementation>>();
            var Pred = new Dictionary<Implementation, HashSet<Implementation>>();
            name2Impl.Values.Iter(impl => Succ.Add(impl, new HashSet<Implementation>()));
            name2Impl.Values.Iter(impl => Pred.Add(impl, new HashSet<Implementation>()));

            foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (var blk in impl.Blocks)
                {
                    foreach (var cmd in blk.Cmds.OfType<CallCmd>())
                    {
                        if (!name2Impl.ContainsKey(cmd.callee)) continue;
                        Succ[impl].Add(name2Impl[cmd.callee]);
                        Pred[name2Impl[cmd.callee]].Add(impl);
                    }
                }
            }

            // Build SCC
            var sccs = new StronglyConnectedComponents<Implementation>(name2Impl.Values,
                new Adjacency<Implementation>(n => Succ[n]),
                new Adjacency<Implementation>(n => Pred[n]));
            sccs.Compute();

            Graph<HashSet<Implementation>> impl_dag = new Graph<HashSet<Implementation>>();     // Construct a new graph where each SCC is reduced to a point, and each node is a set of implementations, which belong to the same SCC
            Dictionary<Implementation, HashSet<Implementation>> impl2set = new Dictionary<Implementation, HashSet<Implementation>>();   // implementation -> SCC containing implementation
            foreach (var scc in sccs)
            {
                var impl_set = new HashSet<Implementation>();
                foreach (var impl in scc)
                {
                    impl_set.Add(impl);
                    impl2set.Add(impl, impl_set);
                }
                impl_dag.AddSource(impl_set);
            }

            foreach (Implementation impl in Succ.Keys)
            {
                foreach (Implementation succ_impl in Succ[impl])
                {
                    if (!impl2set[impl].Equals(impl2set[succ_impl]) && !impl_dag.Edge(impl2set[impl], impl2set[succ_impl])) impl_dag.AddEdge(impl2set[impl], impl2set[succ_impl]);
                }
            }

            // Run topological sort on the reduced call graph
            IEnumerable<HashSet<Implementation>> sortedImplSet;
            sortedImplSet = impl_dag.TopologicalSort();

            if (dbg) Console.WriteLine("{0} {1}", sortedImplSet.First().First().Name, sortedImplSet.First().Count);

            Implementation main = sortedImplSet.First().First();

            var dict = new Dictionary<int, HashSet<string>>();
            dict = transfer_function[new Tuple<Implementation, int>(main, Context_Bound)](dict);

            int count = 0;
            foreach (string query in AnalysisResults.Keys)
            {
                if (AnalysisResults[query])
                {
                    if (dbg) Console.WriteLine(query);
                    count++;
                }
            }
            Console.WriteLine("Removing {0} asserts", count);
        }

        private void buildSCC()
        {
            // Construct the call graph and compute strongly connected components
            var Succ = new Dictionary<Implementation, HashSet<Implementation>>();
            name2Impl.Values.Iter(impl => Succ.Add(impl, new HashSet<Implementation>()));
            name2Impl.Values.Iter(impl => Pred.Add(impl, new HashSet<Implementation>()));

            foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (var blk in impl.Blocks)
                {
                    foreach (var cmd in blk.Cmds.OfType<CallCmd>())
                    {
                        if (!name2Impl.ContainsKey(cmd.callee)) continue;
                        Succ[impl].Add(name2Impl[cmd.callee]);
                        Pred[name2Impl[cmd.callee]].Add(impl);
                    }
                }
            }

            // Build SCC
            stronglyConnectedComponents = new StronglyConnectedComponents<Implementation>(name2Impl.Values,
                new Adjacency<Implementation>(n => Succ[n]),
                new Adjacency<Implementation>(n => Pred[n]));
            stronglyConnectedComponents.Compute();
        }

        public void PreciseMapAnalyze()
        {
            getSingleAllocSites();
            getImplementationsUptoBound();

            foreach (string st in PointsTo.Keys)
            {
                MapPointsTo.Add(st, new HashSet<string>());
                if (!isODotf(st))
                {
                    foreach (string aS in PointsTo[st]) MapPointsTo[st].Add(aS);
                }
            }

            Implementation entry = stronglyConnectedComponents.First().First();

            Dictionary<int, HashSet<string>> dummy_dict = new Dictionary<int, HashSet<string>>();

            dummy_dict = transfer_function[new Tuple<Implementation, int>(entry, Context_Bound)](dummy_dict);
        }

        private void getSingleAllocSites()
        {
            buildSCC();

            HashSet<Implementation> single_impl = new HashSet<Implementation>();

            foreach (var scc in stronglyConnectedComponents)
            {
                if (scc.Count != 1) continue;
                Implementation impl = scc.First();
                if (!Pred.ContainsKey(impl)) single_impl.Add(impl);
                else if (Pred[impl].Count == 0) single_impl.Add(impl);
                else if (!Pred[impl].Contains(impl)) single_impl.Add(impl);
            }
            cmd2AllocationConstraint.Keys.Where(key => single_impl.Contains(key.Item1)).Iter(key => singleAllocSites.Add(cmd2AllocationConstraint[key]));
        }

        private void getImplementationsUptoBound()
        {
            int depth = 0;
            foreach (var scc in stronglyConnectedComponents)
            {
                if (depth == Context_Bound) break;
                if (scc.Count == 1 && !Pred[scc.First()].Contains(scc.First())) implementationsUptoBound.Add(scc.First());
                depth++;
            }
        }

        public static void removeAsserts(Program origProgram, Dictionary<string, bool> csfs_ret)
        {
            foreach (Implementation impl in origProgram.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (Block b in impl.Blocks)
                {
                    var removal_list = new List<AssertCmd>();
                    foreach (Cmd c in b.Cmds)
                    {
                        if (c is AssertCmd)
                        {
                            var ac = c as AssertCmd;
                            if (CleanAssert.validAssert(ac))
                            {
                                if (csfs_ret.ContainsKey(CleanAssert.getQueryFromAssert(ac)) && csfs_ret[CleanAssert.getQueryFromAssert(ac)])
                                {
                                    removal_list.Add(ac);
                                    Console.Write("Removing assert :- {0}", ac.ToString());
                                }
                            }
                        }
                    }
                    foreach (AssertCmd ac in removal_list) b.Cmds.Remove(ac);
                }
            }
        }
    }

    public class ReadSet : FixedVisitor
    {
        HashSet<Tuple<Variable, List<string>>> readSet;
        List<string> currMapSelects;

        private ReadSet()
        {
            readSet = new HashSet<Tuple<Variable, List<string>>>();
            currMapSelects = new List<string>();
        }

        public static HashSet<Tuple<Variable, List<string>>> Get(Expr expr)
        {
            var rs = new ReadSet();
            rs.VisitExpr(expr);
            return rs.readSet;
        }

        public static HashSet<Tuple<Variable, List<string>>> Get(IEnumerable<Expr> exprs)
        {
            var rs = new ReadSet();
            exprs.Iter(expr => rs.VisitExpr(expr));
            return rs.readSet;
        }

        public override Variable VisitVariable(Variable node)
        {
            readSet.Add(Tuple.Create(node, new List<string>(currMapSelects)));
            return base.VisitVariable(node);
        }

        public override Expr VisitNAryExpr(NAryExpr node)
        {
            if (node.Fun is MapSelect)
            {
                currMapSelects.Add((node.Args[0] as IdentifierExpr).Name);
                for (int i = 1; i < node.Args.Count; i++)
                    base.VisitExpr(node.Args[i]);
                currMapSelects.RemoveAt(currMapSelects.Count - 1);
                return node;
            }
            return base.VisitNAryExpr(node);
        }
    }

    public abstract class AliasConstraint
    {
        public abstract void GatherMentionedVars(ref HashSet<string> vars);
    }

    class AssignConstraint : AliasConstraint
    {
        public string source;
        public string target;
        public AssignConstraint(string source, string target)
        {
            this.source = source;
            this.target = target;
        }
        public override void GatherMentionedVars(ref HashSet<string> vars)
        {
            vars.Add(source);
            vars.Add(target);
        }
        public override string ToString()
        {
            return string.Format("{0} := {1}", target, source);
        }
    }

    class AllocationConstraint : AliasConstraint
    {
        public string allocSite;
        public string target;
        public bool full;
        private static int counter = 0;

        public AllocationConstraint(string target, bool full)
        {
            this.allocSite = "allocSite" + (counter++).ToString();
            this.target = target;
            this.full = full;
        }

        public static string getSite()
        {
            return ("allocSite" + counter.ToString());
        }

        public override void GatherMentionedVars(ref HashSet<string> vars)
        {
            vars.Add(target);
        }
        public override string ToString()
        {
            return string.Format("{0} := {1}", target, allocSite);
        }

    }

    class LoadConstraint : AliasConstraint
    {
        public string source;
        public string map;
        public string target;

        public LoadConstraint(string source, string target, string map)
        {
            this.source = source;
            this.target = target;
            this.map = map;
        }

        public override void GatherMentionedVars(ref HashSet<string> vars)
        {
            vars.Add(source);
            vars.Add(target);
        }
        public override string ToString()
        {
            return string.Format("{0} := {1}.{2}", target, source, map);
        }

    }

    class StoreConstraint : AliasConstraint
    {
        public string source;
        public string map;
        public string target;

        public StoreConstraint(string source, string target, string map)
        {
            this.source = source;
            this.target = target;
            this.map = map;
        }

        public override void GatherMentionedVars(ref HashSet<string> vars)
        {
            vars.Add(source);
            vars.Add(target);
        }
        public override string ToString()
        {
            return string.Format("{0}.{1} := {2}", target, map, source);
        }

    }

    public class AliasConstraintSolver
    {
        List<AliasConstraint> constraints;
        Dictionary<string, HashSet<string>> PointsTo;
        Dictionary<string, HashSet<string>> PointsToDelta;
        HashSet<string> maps;
        HashSet<string> worklist;
        bool solved;
        const int environmentPointersUnroll = 0;
        public static bool dbg = false;
        string null_allocSite;

        public AliasConstraintSolver()
        {
            constraints = new List<AliasConstraint>();
            PointsTo = new Dictionary<string, HashSet<string>>();
            PointsToDelta = new Dictionary<string, HashSet<string>>();
            worklist = new HashSet<string>();
            solved = false;
            null_allocSite = null;
        }


        public void Add(AliasConstraint constraint)
        {
            Debug.Assert(!solved);
            if (dbg) Console.WriteLine("Adding constraint: {0}", constraint);
            constraints.Add(constraint);
        }

        public void Solve()
        {
            var sw = new Stopwatch();
            sw.Start();

            // variables
            var variables = new HashSet<string>();
            constraints.Iter(c => c.GatherMentionedVars(ref variables));

            // maps
            maps = new HashSet<string>();
            constraints.OfType<LoadConstraint>()
                .Iter(c => maps.Add(c.map));
            constraints.OfType<StoreConstraint>()
                .Iter(c => maps.Add(c.map));

            // alloc sites
            var allocSites = new HashSet<string>();
            constraints.OfType<AllocationConstraint>()
                .Iter(c => allocSites.Add(c.allocSite));

            // Varible -> store instruction
            var varToStore = new Dictionary<string, List<StoreConstraint>>();
            variables.Iter(v => varToStore.Add(v, new List<StoreConstraint>()));
            constraints.OfType<StoreConstraint>()
                .Iter(s => varToStore[s.target].Add(s));

            // Variable -> load instruction
            var varToLoad = new Dictionary<string, List<LoadConstraint>>();
            variables.Iter(v => varToLoad.Add(v, new List<LoadConstraint>()));
            constraints.OfType<LoadConstraint>()
                .Iter(l => varToLoad[l.source].Add(l));

            InitFullAllocators();

            var G = new Dictionary<string, HashSet<string>>();

            // DoAnalysis
            worklist = new HashSet<string>();
            foreach (var a in constraints.OfType<AllocationConstraint>())
            {
                PointsToDelta.InitAndAdd(a.target, a.allocSite);
                worklist.Add(a.target);
            }
            foreach (var a in constraints.OfType<AssignConstraint>())
                G.InitAndAdd(a.source, a.target);
            while (worklist.Count != 0)
            {
                var n = worklist.First();
                worklist.Remove(n);

                if (!G.ContainsKey(n)) G.Add(n, new HashSet<string>());
                if (!PointsTo.ContainsKey(n)) PointsTo.Add(n, new HashSet<string>());
                if (!PointsToDelta.ContainsKey(n)) PointsToDelta.Add(n, new HashSet<string>());

                G[n].Iter(nprime => DiffProp(PointsToDelta[n], nprime));

                if (variables.Contains(n))
                {
                    foreach (var store in varToStore[n])
                    {
                        var x = store.target;
                        var y = store.source;
                        var f = store.map;
                        var ptd = PointsToDelta[n];
                        foreach (var o in ptd)
                        {
                            var of = GetODotf(o, f);
                            if (!G.ContainsKey(y)) G.Add(y, new HashSet<string>());
                            if (!PointsTo.ContainsKey(y)) PointsTo.Add(y, new HashSet<string>());

                            if (!G[y].Contains(of))
                            {
                                G[y].Add(of);
                                DiffProp(PointsTo[y], of);
                            }

                        }
                    }
                    foreach (var load in varToLoad[n])
                    {
                        var x = load.source;
                        var y = load.target;
                        var f = load.map;
                        var ptd = PointsToDelta[n];
                        foreach (var o in ptd)
                        {
                            var of = GetODotf(o, f);
                            if (!G.ContainsKey(of)) G.Add(of, new HashSet<string>());
                            if (!PointsTo.ContainsKey(of)) PointsTo.Add(of, new HashSet<string>());
                            if (!G[of].Contains(y))
                            {
                                G[of].Add(y);
                                DiffProp(PointsTo[of], y);
                            }
                        }
                    }
                }
                PointsTo[n].UnionWith(PointsToDelta[n]);
                PointsToDelta[n] = new HashSet<string>();
            }
            #region stats
            if (dbg)
            {
                Console.WriteLine("Num constraints: {0}", constraints.Count);
                Console.WriteLine("Num variables, maps, alloc-sites: {0}, {1},  {2}", variables.Count, maps.Count, allocSites.Count);
                Console.WriteLine("Time: {0}", sw.Elapsed.TotalSeconds);
            }
            #endregion
            solved = true;
        }

        public Dictionary<string, HashSet<string>> GetPointsToSet()
        {
            return PointsTo;
        }

        // For debugging only
        public HashSet<string> GetPointsTo(string var)
        {
            return PointsTo[var];
        }
        public HashSet<string> GetPointsTo(string var, string map)
        {
            var ret = new HashSet<string>();
            foreach (var o in PointsTo[var])
            {
                ret.UnionWith(PointsTo[GetODotf(o, map)]);
            }
            return ret;
        }

        private void InitFullAllocators()
        {
            // For "full" allocation sites, add extra constraints
            var fullAllocSites = new HashSet<string>();
            constraints.OfType<AllocationConstraint>().Where(ac => ac.full)
                .Iter(ac => fullAllocSites.Add(ac.allocSite));

            var maps = new HashSet<string>();
            constraints.OfType<LoadConstraint>()
                .Iter(l => maps.Add(l.map));
            constraints.OfType<StoreConstraint>()
                .Iter(l => maps.Add(l.map));

            // For each full alloc site o and map f, add:
            // PointsTo[o.f] = {o_f_i}
            // PointsTo[o_g_i.f] = {o_g_{i+1}}
            // PointsTo[o_g_n.f] = {o_g_n}

            int uid = 0;

            var frontier = new HashSet<string>(fullAllocSites);
            for (int i = 0; i <= environmentPointersUnroll; i++)
            {
                var next = new HashSet<string>();
                foreach (var o in frontier)
                {
                    foreach (var f in maps)
                    {
                        var of = GetODotf(o, f);
                        var nsite = (i == environmentPointersUnroll) ? o : "aa_newEnvSite" + (uid++).ToString();
                        if (!PointsTo.ContainsKey(of))
                            PointsTo.Add(of, new HashSet<string>());
                        PointsTo[of].Add(nsite);
                        next.Add(nsite);
                    }
                }
                frontier = next;
            }
        }


        private string GetODotf(string o, string f)
        {
            return "allocConstruct$" + o + "$" + f;
        }

        private bool isODotf(string of)
        {
            return of.StartsWith("allocConstruct$");
        }

        private Tuple<string, string> deconstructODotf(string of)
        {
            var sp = of.Split('$');
            Debug.Assert(sp.Length == 3);
            return Tuple.Create(sp[1], sp[2]);
        }

        private void DiffProp(HashSet<string> srcSet, string n)
        {
            if (!PointsTo.ContainsKey(n))
                PointsTo.Add(n, new HashSet<string>());
            if (!PointsToDelta.ContainsKey(n))
                PointsToDelta.Add(n, new HashSet<string>());

            var change = new HashSet<string>(srcSet);
            change.ExceptWith(PointsTo[n]);

            // If the variable is known to be non null, remove allocation site of NULL from its points to set
            if (PointsTo.ContainsKey("NULL") && PointsTo["NULL"].Count > 0)
            {
                null_allocSite = PointsTo["NULL"].First();
                if (AliasAnalysis.non_null_vars.Contains(n)) change.Remove(null_allocSite);
            }
            
            if (!change.IsSubsetOf(PointsToDelta[n]))
                worklist.Add(n);

            PointsToDelta[n] = PointsToDelta[n].Union(change);
        }

        public bool IsAlias(string var1, string var2)
        {
            Debug.Assert(solved);
            if (!PointsTo.ContainsKey(var1) || PointsTo[var1].Count == 0)
            {
                if(dbg) Console.WriteLine("Warning: empty points-to set for {0}", var1);
                return false;
            }

            if (!PointsTo.ContainsKey(var2) || PointsTo[var2].Count == 0)
            {
                if (dbg) Console.WriteLine("Warning: empty points-to set for {0}", var2);
                return false;
            }

            return PointsTo[var1].Intersection(PointsTo[var2]).Any();
        }

        // Does var1 || var1->f1 || var1->f1->f2 ... == var2?
        public bool IsReachable(string var1, string var2)
        {
            Debug.Assert(solved);

            if (!PointsTo.ContainsKey(var1) || !PointsTo.ContainsKey(var2) || PointsTo[var1].Count == 0 || PointsTo[var2].Count == 0)
                return false;

            // compute closure under load
            var reachable = new HashSet<string>(PointsTo[var1]);
            var delta = new HashSet<string>(reachable);
            while (delta.Any())
            {
                var next = new HashSet<string>();
                foreach (var o in delta)
                {
                    foreach (var m in maps)
                    {
                        var om = GetODotf(o, m);
                        if (PointsTo.ContainsKey(om))
                        {
                            next.UnionWith(PointsTo[om]);
                        }
                    }
                }
                reachable.UnionWith(next);
                delta = next;
                delta.ExceptWith(reachable);
            }

            return reachable.Intersection(PointsTo[var2]).Any();
        }

        // Return the set of allocation sites of v
        public HashSet<string> AllocationSites(string v)
        {
            Debug.Assert(solved);
            var ret = new HashSet<string>();

            if (!PointsTo.ContainsKey(v))
                return ret;

            return PointsTo[v];
        }
    }

    public static class InitDictionary
    {
        public static void InitAndAdd<K, V>(this Dictionary<K, HashSet<V>> map, K key, V value)
        {
            if (!map.ContainsKey(key)) map.Add(key, new HashSet<V>());
            map[key].Add(value);
        }
    }
}
