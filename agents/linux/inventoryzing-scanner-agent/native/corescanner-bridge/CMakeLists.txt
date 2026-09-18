cmake_minimum_required(VERSION 3.16)
project(inventoryzing_zebra_bridge LANGUAGES CXX)

add_executable(inventoryzing-zebra-bridge main.cpp)
target_compile_features(inventoryzing-zebra-bridge PRIVATE cxx_std_17)
target_include_directories(inventoryzing-zebra-bridge PRIVATE /usr/include/zebra-scanner)
target_link_directories(inventoryzing-zebra-bridge PRIVATE /usr/lib/zebra-scanner/corescanner)
target_link_libraries(inventoryzing-zebra-bridge PRIVATE cs-client)

install(TARGETS inventoryzing-zebra-bridge RUNTIME DESTINATION bin)
