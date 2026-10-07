U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "^  $asset,IntegerGrid" sw_$tag.log) $(grep -a -m1 -o 'sing=[0-9]*' sw_$tag.log) $(grep -a 'error CS' sw_$tag.log | head -2)"; }
run base head
run h6 head -orientHier 6
run h6f20 head -orientHier 6 -orientIters 20
run h20 head -orientHier 20
