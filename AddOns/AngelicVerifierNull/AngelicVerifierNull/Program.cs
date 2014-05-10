﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Boogie;
using btype = Microsoft.Boogie.Type;
using cba.Util;
using PersistentProgram = cba.PersistentCBAProgram;

namespace AngelicVerifierNull
{
    class InputProgramDoesNotMatchExn : Exception
    {
        public InputProgramDoesNotMatchExn(string s) : base(s) { } 
    }

    public class Utils
    {
        //TODO: merge with Log class in Corral
        const bool SUPPRESS_DEBUG_MESSAGES = false;
        public enum PRINT_TAG { AV_WARNING, AV_DEBUG, AV_OUTPUT };
        public static void Print(string msg, PRINT_TAG tag=PRINT_TAG.AV_DEBUG)
        {
            if (tag != PRINT_TAG.AV_DEBUG || !SUPPRESS_DEBUG_MESSAGES)
                Console.WriteLine("[TAG: {0}] {1}", tag, msg);
        }
    }
    class Driver
    {
        static cba.Configs corralConfig = null;
        static cba.AddUniqueCallIds addIds = null;

        const string CORRAL_MAIN_PROC = "CorralMain";

        public enum PRINT_TRACE_MODE { Boogie, Sdv };
        public static PRINT_TRACE_MODE printTraceMode = PRINT_TRACE_MODE.Boogie;


        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: AngelicVerifierNull file.bpl");
                return;
            }

            if (args.Any(s => s == "/break"))
                System.Diagnostics.Debugger.Launch();

            if (args.Any(s => s == "/sdv"))
                printTraceMode = PRINT_TRACE_MODE.Sdv;

            // Initialize Boogie and Corral
            corralConfig = InitializeCorral();

            PersistentProgram prog = null;
            try
            {
                // Get input program with the harness
                Utils.Print(String.Format("----- Analyzing {0} ------", args[0]), Utils.PRINT_TAG.AV_OUTPUT);
                prog = GetProgram(args[0]);

                // Run alias analysis
                prog = RunAliasAnalysis(prog);

                // Run Corral outer loop
                RunCorralIterative(prog, corralConfig.mainProcName);
            }
            catch (Exception e)
            {
                Utils.Print(String.Format("AnglelicVerifier failed with: {0}", e.Message), Utils.PRINT_TAG.AV_OUTPUT);
            }
        }

        #region Instrumentatations
        //globals
        static Instrumentations.MallocInstrumentation mallocInstrumentation = null;
        /// <summary>
        /// TODO: Check that the input program satisfies some sanity requirements
        /// NULL is declared as constant
        /// malloc is declared as a procedure, with alloc
        /// each parameter/global/map is annotated with "pointer/ref/data"
        /// </summary>
        /// <param name="init"></param>
        private static void CheckInputProgramRequirements(Program init)
        {
            return;
        }
        static PersistentProgram GetProgram(string filename)
        {
            Program init = BoogieUtil.ReadAndOnlyResolve(filename);

            //Sanity check (currently most of it happens inside HarnessInstrumentation)
            CheckInputProgramRequirements(init);

            //Instrument to create the harness
            corralConfig.mainProcName = CORRAL_MAIN_PROC;
            (new Instrumentations.HarnessInstrumentation(init, corralConfig.mainProcName)).DoInstrument();

            //resolve+typecheck wo bothering about modSets
            CommandLineOptions.Clo.DoModSetAnalysis = true;
            init = BoogieUtil.ReResolve(init);
            CommandLineOptions.Clo.DoModSetAnalysis = false;

            // Update mod sets
            ModSetCollector.DoModSetAnalysis(init);

            //TODO: Perform alias analysis here and prune a subset of asserts

            //Various instrumentations on the well-formed program

            mallocInstrumentation = new Instrumentations.MallocInstrumentation(init);
            mallocInstrumentation.DoInstrument();
            //(new Instrumentations.AssertGuardInstrumentation(init)).DoInstrument(); //we don't guard asserts as we turn off the assert explicitly

            //Print the instrumented program
            BoogieUtil.PrintProgram(init, "corralMain.bpl");

            //Do corral specific passes
            GlobalCorralSpecificPass(init);
            var inputProg = new PersistentProgram(init, corralConfig.mainProcName, 1);
            ProgTransformation.PersistentProgram.FreeParserMemory();

            return inputProg;
        }
        #endregion

        #region Corral related
        // Initialization
        static cba.Configs InitializeCorral()
        {
            // 
            CommandLineOptions.Install(new CommandLineOptions());
            CommandLineOptions.Clo.PrintInstrumented = true;

            // Set all defaults for corral
            var config = cba.Configs.parseCommandLine(new string[] { 
                "doesntExist.bpl", "/useProverEvaluate", "/prevCorralState:cstate.db", "/dumpCorralState:cstate.db" });

            cba.Driver.Initialize(config);

            cba.VerificationPass.usePruning = false;

            if (!config.useProverEvaluate)
            {
                cba.ConfigManager.progVerifyOptions.StratifiedInliningWithoutModels = true;
                if (config.NonUniformUnfolding && !config.useProverEvaluate)
                    cba.ConfigManager.progVerifyOptions.StratifiedInliningWithoutModels = false;
                if (config.printData == 0)
                    cba.ConfigManager.pathVerifyOptions.StratifiedInliningWithoutModels = true;
            }

            return config;
        }
        // Make a pass to ensure the whole program created is well formed
        private static void GlobalCorralSpecificPass(Program init)
        {
            // Find main
            List<string> entrypoints = cba.EntrypointScanner.FindEntrypoint(init);
            if (entrypoints.Count == 0)
                throw new InvalidInput("Main procedure not specified");
            corralConfig.mainProcName = entrypoints[0];

            if (BoogieUtil.findProcedureImpl(init.TopLevelDeclarations, corralConfig.mainProcName) == null)
            {
                throw new InvalidInput("Implementation of main procedure not found");
            }

            Debug.Assert(cba.SequentialInstrumentation.isSingleThreadProgram(init, corralConfig.mainProcName));
            cba.GlobalConfig.isSingleThreaded = true;

            // Massage the input program
            addIds = new cba.AddUniqueCallIds();
            addIds.VisitProgram(init);
        }
        //Run Corral over different assertions (modulo errorLimit)
        private static void RunCorralIterative(PersistentProgram prog, string p)
        {
            int iterCount = 0;
            //We are not using the guards to turn the asserts, we simply rewrite the assert
            while (true)
            {
                var cex = RunCorral(prog, corralConfig.mainProcName);
                var traceType = "";

                if (cex == null)
                {
                    //TODO (how do I distinguish inconclusive results from Corral)
                    Console.WriteLine("No more counterexamples found, Corral returns verified...");
                    break;
                }
                //get the pathProgram
                cba.SDVConcretizePathPass concretize;
                var pprog = GetPathProgram(cex.Item1, prog, out concretize);
                //pprog.writeToFile("path" + iterCount + ".bpl");
                var ppprog = pprog.getProgram(); //don't do getProgram on pprog anymore
                var mainImpl = BoogieUtil.findProcedureImpl(ppprog.TopLevelDeclarations, pprog.mainProcName);                
                //call ExplainError 
                var eeStatus = CheckWithExplainError(ppprog, mainImpl,concretize);
                //get the program once 
                var nprog = prog.getProgram();
                
                if (eeStatus.Item1 == REFINE_ACTIONS.SUPPRESS ||
                    eeStatus.Item1 == REFINE_ACTIONS.SHOW_AND_SUPPRESS)
                {
                    traceType = (eeStatus.Item1 == REFINE_ACTIONS.SUPPRESS) ? "Suppressed" : "Angelic";

                    var ret = SupressFailingAssert(nprog, cex.Item2);
                    if (ret == null)
                    {
                        Console.WriteLine("Failure is not an assert, skipping...");
                        Debug.Assert(false);
                        continue;
                    }
                    else
                    {
                        var output = string.Format("Assertion failed in proc {0} at line {1} with expr {2}", cex.Item2.procName, ret.Line, ret.ToString());
                        Console.WriteLine(output);
                        if (eeStatus.Item1 == REFINE_ACTIONS.SHOW_AND_SUPPRESS)
                            Utils.Print(String.Format("ANGELIC_VERIFIER_WARNING: {0}", output),Utils.PRINT_TAG.AV_OUTPUT);
                    }
                }
                else if (eeStatus.Item1 == REFINE_ACTIONS.BLOCK_PATH)
                {
                    traceType = "Blocked";
                    var mainProc = nprog.TopLevelDeclarations.OfType<Procedure>().Where(x => x.Name == corralConfig.mainProcName).FirstOrDefault();
                    if (mainProc == null)
                        throw new Exception(String.Format("Cannot find the main procedure {0} to add blocking requires", corralConfig.mainProcName));
                    mainProc.Requires.Add(new Requires(false, eeStatus.Item2)); //add the blocking condition and iterate
                }

                // print the trace to disk
                PrintTrace(cex.Item1, prog, traceType + iterCount);

                prog = new PersistentProgram(nprog, corralConfig.mainProcName, 1);
                //Print the instrumented program
                iterCount++;
                BoogieUtil.PrintProgram(prog.getProgram(), "corralMain_after_iteration_" + iterCount + ".bpl");
            }
        }

        // print trace to disk
        public static void PrintTrace(cba.ErrorTrace trace, PersistentProgram program, string name)
        {
            if(printTraceMode == PRINT_TRACE_MODE.Boogie) 
                cba.PrintProgramPath.print(program, trace, name);
            else
                cba.PrintSdvPath.Print(program.getProgram(), trace, new HashSet<string>(), null, name + ".tt", "stack.txt");
        }

        // Returns the failing assertion, and supresses it in the input program
        private static AssertCmd SupressFailingAssert(Program program, cba.AssertLocation aloc)
        {
            // find procedure
            var impl = BoogieUtil.findProcedureImpl(program.TopLevelDeclarations, aloc.procName);
            // find block
            var block = impl.Blocks.Where(blk => blk.Label == aloc.blockName).First();
            // find instruction
            Debug.Assert(block.Cmds[aloc.instrNo] is AssertCmd);
            var ret = block.Cmds[aloc.instrNo] as AssertCmd;
            // block assert
            block.Cmds[aloc.instrNo] = new AssumeCmd(ret.tok, ret.Expr, ret.Attributes);

            return ret;
        }
        static cba.CorralState corralState = null;
        // Run Corral on a sequential Boogie Program
        // Returns the error trace and the failing assert location
        static Tuple<cba.ErrorTrace, cba.AssertLocation> RunCorral(PersistentProgram inputProg, string main)
        {
            Debug.Assert(cba.GlobalConfig.isSingleThreaded);
            Debug.Assert(cba.GlobalConfig.InferPass == null);

            // Reuse previous corral state
            if (corralState != null)
                cba.CorralState.AbsorbPrevState(corralConfig, cba.ConfigManager.progVerifyOptions);

            // Rewrite assert commands
            var apass = new cba.RewriteAssertsPass();
            var curr = apass.run(inputProg);

            // Rewrite call commands 
            var rcalls = new cba.RewriteCallCmdsPass(true);
            curr = rcalls.run(curr);

            // Prune
            cba.PruneProgramPass.RemoveUnreachable = true;
            var prune = new cba.PruneProgramPass(false);
            curr = prune.run(curr);
            cba.PruneProgramPass.RemoveUnreachable = false;

            // Sequential instrumentation
            var seqInstr = new cba.SequentialInstrumentation();
            curr = seqInstr.run(curr);

            ProgTransformation.PersistentProgram.FreeParserMemory();

            // For debugging, create an Action for printing a trace at the source level
            var passes = new List<cba.CompilerPass>(new cba.CompilerPass[] { seqInstr, prune, rcalls, apass });
            var printTrace = new Action<cba.ErrorTrace, string>((trace, fileName) =>
            {
                if (!cba.GlobalConfig.genCTrace)
                    return;
                passes.Where(p => p != null)
                    .Iter(p => trace = p.mapBackTrace(trace));
                cba.PrintConcurrentProgramPath.printCTrace(inputProg, trace, fileName);
                apass.reset();
            });


            ////////////////////////////////////
            // Verification phase
            ////////////////////////////////////

            var refinementState = new cba.RefinementState(curr, new HashSet<string>(corralConfig.trackedVars.Union(new string[] { seqInstr.assertsPassedName })), false);

            cba.ErrorTrace cexTrace = null;
            cba.Driver.checkAndRefine(curr, refinementState, printTrace, out cexTrace);

            // dump corral state for next iteration
            cba.CorralState.DumpCorralState(corralConfig, cba.ConfigManager.progVerifyOptions.CallTree,
                refinementState.getVars().Variables);

            ////////////////////////////////////
            // Output Phase
            ////////////////////////////////////

            if (cexTrace != null)
            {
                cexTrace = seqInstr.mapBackTrace(cexTrace);
                cexTrace = prune.mapBackTrace(cexTrace);
                cexTrace = rcalls.mapBackTrace(cexTrace);
                //PrintProgramPath.print(rcalls.input, cexTrace, "temp0");
                cexTrace = apass.mapBackTrace(cexTrace);
                return Tuple.Create(cexTrace, apass.getFailingAssertLocation());
            }

            return null;
        }
        // Given a counterexample trace 'trace' through a program 'program', return the
        // path program for that trace. The path program has a single implementation 
        // with straightline code, and all non-determinism is concretized
        static PersistentProgram GetPathProgram(cba.ErrorTrace trace, PersistentProgram program, out cba.SDVConcretizePathPass concretize)
        {
            BoogieVerify.options = cba.ConfigManager.pathVerifyOptions;

            // convert trace to a path program
            cba.RestrictToTrace.convertNonFailingAssertsToAssumes = true;
            var tinfo = new cba.InsertionTrans();
            var traceProgCons = new cba.RestrictToTrace(program.getProgram(), tinfo);
            traceProgCons.addTrace(trace);
            var tprog = traceProgCons.getProgram();
            cba.RestrictToTrace.convertNonFailingAssertsToAssumes = false;

            // mark some annotations (that enable optimizations) along the path program
            cba.Driver.sdvAnnotateDefectTrace(tprog, corralConfig);

            // convert to a persistent program
            var witness = new cba.PersistentCBAProgram(tprog, traceProgCons.getFirstNameInstance(program.mainProcName), 0);
            // rewrite asserts back to main
            witness = cba.DeepAssertRewrite.InstrumentTrace(witness);

            witness.writeToFile("trace_prog.bpl");

            // Concretize non-determinism
            BoogieVerify.options = cba.ConfigManager.pathVerifyOptions;
            concretize = new cba.SDVConcretizePathPass(addIds.callIdToLocation);
            witness = concretize.run(witness); //uncomment: shuvendu

            witness.getProgram().Resolve();
            witness.getProgram().Typecheck();

            if (concretize.success)
            {
                // Something went wrong, fail 
                throw new Exception("Path program concretization failed");
            }

            // optinally, dump the witness to a file
            // witness.writeToFile("corral_witness.bpl");

            return witness;
        }
        #endregion

        #region ExplainError related
        private enum REFINE_ACTIONS { SHOW_AND_SUPPRESS, SUPPRESS, BLOCK_PATH };
        private static Tuple<REFINE_ACTIONS,Expr> CheckWithExplainError(Program nprog, Implementation mainImpl, 
            cba.SDVConcretizePathPass concretize)
        {
            //Let ee be the result of ExplainError
            // if (ee is SUCCESS && ee is True) ShowWarning; Suppress 
            // else if (ee is SUCCESS(e)) Block(e); 
            // else //inconclusive/timeout/.. Suppress
            var status = Tuple.Create(REFINE_ACTIONS.SUPPRESS, (Expr)Expr.True); //default is SUPPRESS (angelic)
            ExplainError.STATUS eeStatus = ExplainError.STATUS.INCONCLUSIVE;

            Dictionary<string, string> eeComplexExprs;
            try
            {
                HashSet<List<Expr>> preDisjuncts;
                var explain = ExplainError.Toplevel.Go(mainImpl, nprog, 1000, 1, out eeStatus, out eeComplexExprs, out preDisjuncts);
                Utils.Print(String.Format("The output of ExplainError => Status = {0} Exprs = ({1})",
                    eeStatus, explain != null ? String.Join(", ", explain) : ""));
                if (eeStatus == ExplainError.STATUS.SUCCESS)
                {
                    if (explain.Count == 1 && explain[0].TrimEnd(new char[]{' ', '\t'}) == Expr.True.ToString())
                        status = Tuple.Create(REFINE_ACTIONS.SHOW_AND_SUPPRESS, (Expr) Expr.True);
                    else if (explain.Count > 0)
                    {
                        var blockExpr = Expr.Not(ExplainError.Toplevel.ExprListSetToDNFExpr(preDisjuncts));
                        blockExpr = MkBlockExprFromExplainError(nprog, blockExpr, concretize.allocConstants);
                        Utils.Print(String.Format("EXPLAINERROR-BLOCK :: {0}", blockExpr), Utils.PRINT_TAG.AV_OUTPUT);
                        status = Tuple.Create(REFINE_ACTIONS.BLOCK_PATH, blockExpr); 
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("ExplainError failed with {0}", e);
            }
            return status;
        }
        private static Expr MkBlockExprFromExplainError(Program  nprog, Expr expr, Dictionary<string, Tuple<string, string, int>> allocConsts)
        {
            //- given expr, allocConsts (string)
            //- usedVars = varsUsedIn(expr)
            //- newUsedVars = Lookup(usedVars, pprog)  // subset of newAllocConsts U NULL     
            //- newAllocConsts= Lookup(allocConsts, pprog)
            //- allocToBv: newAllocConsts -> bv 
            //- allocToTriggers: newAllocConsts -> triggers (all in pprog)

            //- forallBV = newUsedVars \intersect newAllocConsts
            //  forallPre = \wedge_i (trigger(bv) | bv \in forallBV)
            //  forallPost = expr[newUsedVars/usedVars][allocToBV/newAllocConsts]
            //- forall forallBV :: forallPre => forallPost

            Utils.Print(String.Format("The list of allocConsts along trace = {0}", String.Join(", ",
                        allocConsts
                        .Select(x => "(" + x.Key + " -> [" + x.Value.Item1 + ", " + x.Value.Item2 + ", " + x.Value.Item3 + "])"))
            ));
            Dictionary<string, Tuple<Variable, Expr>> allocToBndVarAndTrigger = new Dictionary<string, Tuple<Variable, Expr>>();
            int allocConstCount = 0;
            allocConsts.ToList()
                .ForEach(x =>
                    {
                        var xConst = nprog.TopLevelDeclarations.OfType<Constant>().Where(y => y.Name == x.Key).FirstOrDefault();
                        if (xConst == null)
                            throw new Exception(String.Format("WARNING!!: Cannot find constant with the name {0}", x.Key));
                        allocConstCount++;
                        string mallocTrigger;
                        //sometimes Corral creates an alloc_k variable for non ":allocator" calls, skip them
                        if (!mallocInstrumentation.mallocTriggersLocation.TryGetValue(x.Value, out mallocTrigger))
                            throw new Exception(String.Format("WARNING!!: allocConst {0} has no mallocTrigger", x.Key));
                        var mallocTriggerFn = nprog.TopLevelDeclarations.OfType<Function>().Where(y => y.Name == mallocTrigger).FirstOrDefault();
                        if (mallocTriggerFn == null)
                            throw new Exception(String.Format("WARNING!!: Current program has no mallocTrigger with name", mallocTrigger));
                        //create a new bound variable for quantified expr later
                        var bvar =  new BoundVariable(Token.NoToken, 
                                        new TypedIdent(Token.NoToken, "x_" +  allocConstCount, Microsoft.Boogie.Type.Int));
                        //make an expr mallocFn(x_i) for alloc_i
                        var fnApp = (Expr) new NAryExpr(Token.NoToken,
                                new FunctionCall(mallocTriggerFn),
                                new List<Expr> () {IdentifierExpr.Ident(bvar)});
                        allocToBndVarAndTrigger[xConst.Name] = Tuple.Create((Variable) bvar,  fnApp);
                    });

            var usedVarsCollector = new VariableCollector();
            usedVarsCollector.Visit(expr);
            Utils.Print(string.Format("List of used vars in {0} => {1}", expr, String.Join(", ", usedVarsCollector.usedVars)));

            var nexpr = (new Instrumentations.RewriteConstants(usedVarsCollector.usedVars)).VisitExpr(expr); //get the expr in scope of pprog
            Debug.Assert(expr.ToString() == nexpr.ToString(), "Unexpected difference introduced during porting expression to current program");

            var substMap = new Dictionary<Variable, Expr>();
            var forallPre = new List<Expr>();
            List<Variable> bvarList = new List<Variable>(); //only bound variables used in the expression
            usedVarsCollector.usedVars.Iter(x =>
            {
                if (allocToBndVarAndTrigger.ContainsKey(x.Name))
                {
                    substMap[x] = (Expr) IdentifierExpr.Ident(allocToBndVarAndTrigger[x.Name].Item1);
                    forallPre.Add(allocToBndVarAndTrigger[x.Name].Item2);
                    bvarList.Add(allocToBndVarAndTrigger[x.Name].Item1);
                }
            });
            Substitution subst = Substituter.SubstitutionFromHashtable(substMap);
            nexpr = Substituter.Apply(subst, nexpr);
            Utils.Print(string.Format("The substituted expression for {0} is {1}", expr, nexpr));

            //create the forall (forall x_i..: malloc_Trigger_i(x_i) .. => expr')
            var forallPreExpr = forallPre.Aggregate(
                                        (Expr) Expr.True,
                                        (x, y) => ExplainError.Toplevel.ExprUtil.And(x, y));
            var forallBody = Expr.Imp(forallPreExpr, nexpr);
            //Utils.Print(string.Format("The body of forall {0}", forallBody));

            Expr forallExpr;
            if (bvarList.Count == 0)
            {
                Utils.Print(String.Format("The expression has no free allocated variables {0}", forallBody), Utils.PRINT_TAG.AV_WARNING);
                forallExpr = forallBody;
            }
            else
            {
                forallExpr = new ForallExpr(Token.NoToken, bvarList, forallBody);
            }
            return forallExpr;
        }
        #endregion

        #region Alias analysis

        // Run Alias Analysis on a sequential Boogie program
        // and returned the pruned program
        static PersistentProgram RunAliasAnalysis(PersistentProgram inp)
        {
            var program = inp.getProgram();

            // Make sure that aliasing queries are on identifiers only
            AliasAnalysis.SimplifyAliasingQueries.Simplify(program);

            // Do SSA
            program =
              SSA.Compute(program, PhiFunctionEncoding.Verifiable, new HashSet<string> { "int" });

            var ret =
              AliasAnalysis.AliasAnalysis.DoAliasAnalysis(program);

            foreach (var tup in ret)
                Console.WriteLine("{0}: {1}", tup.Key, tup.Value);

            var origProgram = inp.getProgram();
            AliasAnalysis.PruneAliasingQueries.Prune(origProgram, ret);

            return new PersistentProgram(origProgram, inp.mainProcName, inp.contextBound);
        }
        #endregion
    }
}
