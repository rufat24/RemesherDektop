U="/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe"
run(){ tag=$1; asset=$2; shift 2; "$U" -batchmode -nographics -projectPath Headless -executeMethod NativeRemesher.Benchmark.EditorParity.Run -asset $asset -extractor IntegerGrid -targets 4600,5000,5400 -tag _$tag "$@" -quit -logFile sw_$tag.log; echo "$tag $(grep -a -m1 "MEAN" sw_$tag.log) sing=$(grep -a -o 'sing=[0-9]*' sw_$tag.log | sort -u | tr '\n' ' ') $(grep -a 'error CS' sw_$tag.log | head -2)"; }

















run e_base_head head
run e_r3_head head -curvAlign 0.1 -curvRadius 3
run e_r3t_head head -curvAlign 0.3 -curvRadius 3 -curvStart 0.3 -curvFull 1.0
run e_base_body body
run e_r3_body body -curvAlign 0.1 -curvRadius 3
run e_r3t_body body -curvAlign 0.3 -curvRadius 3 -curvStart 0.3 -curvFull 1.0
