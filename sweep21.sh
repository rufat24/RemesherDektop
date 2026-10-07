U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "== $tag"; grep -a "^  $asset,\|adaptive quad count\|error CS\|threw\|Exception" sw_$tag.log | sort -u | cut -c1-300; grep -a -o "edges(open/nm) close=[0-9/]* end=[0-9/]*" sw_$tag.log | sort -u; }
run ccnb ccnb -targets 5000
run mr3 body -targets 5000 -maxRatio 3
run mr3c body -targets 5000 -maxRatio 3 -curves
echo SWEEP21_DONE
