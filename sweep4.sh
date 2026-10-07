U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "^  $asset,IntegerGrid" sw_$tag.log) $(grep -a -m1 -o 'sing=[0-9]*' sw_$tag.log) $(grep -a -m1 -o 'smoothed over [0-9]* sweeps' sw_$tag.log) $(grep -a 'error CS' sw_$tag.log | head -2)"; }
run ns1 head -normSmooth 1
run ns1h head -normSmooth 1 -orientHier 6 -orientIters 20
run ns2h head -normSmooth 2 -orientHier 6 -orientIters 20
run ns05h head -normSmooth 0.5 -orientHier 6 -orientIters 20
