U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) sing=$(grep -a -o 'sing=[0-9]*' sw_$tag.log | sort -u | tr '\n' ' ') $(grep -a 'error CS' sw_$tag.log | head -2)"; }




run s1h head -normSweeps 1 -orientHier 6 -orientIters 20
run s2h head -normSweeps 2 -orientHier 6 -orientIters 20
run s1 head -normSweeps 1
run s1h6 head -normSweeps 1 -orientHier 6
